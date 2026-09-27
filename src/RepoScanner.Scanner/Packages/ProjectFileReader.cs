using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using System.Xml;
using System.Xml.Linq;
using Microsoft.Extensions.Logging;
using RepoScanner.Scanner.Files;

namespace RepoScanner.Scanner.Packages
{
    // Lists the NuGet packages the project files under a folder declare, each at the file and line where
    // its version is set (Issue 5 design, Reading project files). Reads .csproj files and, for Central
    // Package Management, Directory.Packages.props. Nothing is evaluated: no properties, conditions or imports.
    public sealed class ProjectFileReader
    {
        public const string PackagesPropsFileName = "Directory.Packages.props";

        // A scanned repository is untrusted: no DTD, so no entity expansion, and no resolver, so nothing is
        // fetched (Issue 5 design, decision 3). A file with a DTD is reported as not valid XML.
        private static readonly XmlReaderSettings XmlSettings = new XmlReaderSettings
        {
            DtdProcessing = DtdProcessing.Prohibit,
            XmlResolver = null
        };

        private readonly RepositoryFileWalker _walker;
        private readonly ILogger<ProjectFileReader> _logger;

        public ProjectFileReader(RepositoryFileWalker walker, ILogger<ProjectFileReader> logger)
        {
            _walker = walker;
            _logger = logger;
        }

        public async Task<ProjectFileReadResult> ReadAsync(string rootPath, CancellationToken cancellationToken)
        {
            List<(RepositoryFile File, XDocument Document)> projects = new List<(RepositoryFile File, XDocument Document)>();

            // Keyed by the folder holding the props file, relative to the scanned folder ("" for the root)
            Dictionary<string, PackagesProps> propsByFolder = new Dictionary<string, PackagesProps>(StringComparer.Ordinal);
            int projectFileCount = 0;
            int invalidFileCount = 0;

            foreach (RepositoryFile file in _walker.GetFiles(rootPath))
            {
                bool isProject = file.RelativePath.EndsWith(".csproj", StringComparison.OrdinalIgnoreCase);
                bool isProps = FileNameOf(file.RelativePath).Equals(PackagesPropsFileName, StringComparison.OrdinalIgnoreCase);
                if (!isProject && !isProps)
                {
                    continue;
                }

                projectFileCount++;

                // Null when the file cannot be read now; the walker has logged a warning naming it
                string? text = await _walker.TryReadTextAsync(file, cancellationToken);
                if (text == null)
                {
                    continue;
                }

                XDocument? document = TryParse(file.RelativePath, text);
                if (document == null)
                {
                    invalidFileCount++;
                }
                else if (isProject)
                {
                    projects.Add((file, document));
                }
                else
                {
                    propsByFolder[FolderOf(file.RelativePath)] = PackagesProps.Read(file.RelativePath, document);
                }
            }

            // A set, so a <PackageVersion> used by several projects is declared once
            HashSet<DeclaredPackage> declared = new HashSet<DeclaredPackage>(propsByFolder.Values.SelectMany(props => props.GlobalReferences));
            int missingVersionCount = 0;
            int propertyVersionCount = 0;

            foreach ((RepositoryFile file, XDocument document) in projects)
            {
                foreach (XElement reference in Items(document, "PackageReference"))
                {
                    // Update changes a reference made elsewhere, so only Include is a reference
                    string? packageId = ((string?)reference.Attribute("Include"))?.Trim();
                    if (string.IsNullOrEmpty(packageId))
                    {
                        continue;
                    }

                    if (IsProperty(packageId))
                    {
                        propertyVersionCount++;
                        continue;
                    }

                    string? version = Metadata(reference, "VersionOverride") ?? Metadata(reference, "Version");
                    DeclaredPackage? package = version != null
                        ? new DeclaredPackage(file.RelativePath, LineOf(reference), packageId, version)
                        : CentralVersion(propsByFolder, FolderOf(file.RelativePath), packageId);

                    if (package == null)
                    {
                        missingVersionCount++;
                    }
                    else
                    {
                        declared.Add(package);
                    }
                }
            }

            // Counted after removing repeats, so a property version in the props file used by several
            // projects counts once
            propertyVersionCount += declared.RemoveWhere(package => IsProperty(package.PackageId) || IsProperty(package.VersionText));

            List<DeclaredPackage> packages = declared
                .OrderBy(package => package.FilePath, StringComparer.Ordinal)
                .ThenBy(package => package.LineNumber)
                .ToList();

            return new ProjectFileReadResult(packages, projectFileCount, invalidFileCount, propertyVersionCount, missingVersionCount);
        }

        // Null, with a warning, when the text is not valid XML or has a DTD. The warning gives the line and
        // position only: the exception's message can quote the file.
        private XDocument? TryParse(string relativePath, string text)
        {
            try
            {
                using (StringReader textReader = new StringReader(text))
                using (XmlReader xmlReader = XmlReader.Create(textReader, XmlSettings))
                {
                    return XDocument.Load(xmlReader, LoadOptions.SetLineInfo);
                }
            }
            catch (XmlException exception)
            {
                _logger.LogWarning("Skipped {FilePath}: it is not valid XML (line {LineNumber}, position {LinePosition})",
                    relativePath, exception.LineNumber, exception.LinePosition);
                return null;
            }
        }

        // An MSBuild property such as $(JsonVersion), which is not evaluated, so the value is unknown
        private static bool IsProperty(string value)
        {
            return value.Contains("$(", StringComparison.Ordinal);
        }

        // The version set for the package in the nearest Directory.Packages.props: MSBuild uses the first one
        // it finds walking up from the project's folder, and only that one. Null when there is none.
        private static DeclaredPackage? CentralVersion(Dictionary<string, PackagesProps> propsByFolder, string projectFolder, string packageId)
        {
            string? folder = projectFolder;
            while (folder != null)
            {
                if (propsByFolder.TryGetValue(folder, out PackagesProps? props))
                {
                    return props.Versions.GetValueOrDefault(packageId);
                }

                folder = ParentOf(folder);
            }

            return null;
        }

        // Every element of one item type, such as PackageReference, whatever namespace the project uses
        private static IEnumerable<XElement> Items(XDocument document, string itemType)
        {
            return document.Descendants().Where(element => element.Name.LocalName == itemType);
        }

        // An item's metadata, such as Version, which MSBuild takes from the attribute or else from a child
        // element of that name: <Version>13.0.3</Version>. Trimmed; null when absent or empty.
        private static string? Metadata(XElement item, string name)
        {
            string? value = (string?)item.Attribute(name)
                ?? item.Elements().Where(child => child.Name.LocalName == name).Select(child => child.Value).FirstOrDefault();

            return string.IsNullOrWhiteSpace(value) ? null : value.Trim();
        }

        private static int LineOf(XElement element)
        {
            return ((IXmlLineInfo)element).LineNumber;
        }

        private static string FileNameOf(string relativePath)
        {
            return relativePath.Substring(relativePath.LastIndexOf('/') + 1);
        }

        // The folder part of a relative path with / between folders: "" for a file in the scanned folder
        private static string FolderOf(string relativePath)
        {
            int slash = relativePath.LastIndexOf('/');
            return slash < 0 ? string.Empty : relativePath.Substring(0, slash);
        }

        // Null above the scanned folder
        private static string? ParentOf(string folder)
        {
            return folder.Length == 0 ? null : FolderOf(folder);
        }

        // The versions one Directory.Packages.props sets, keyed by package ID ignoring case, as NuGet
        // compares IDs, and the references it adds to every project below it
        private sealed class PackagesProps
        {
            private PackagesProps(Dictionary<string, DeclaredPackage> versions, IReadOnlyList<DeclaredPackage> globalReferences)
            {
                Versions = versions;
                GlobalReferences = globalReferences;
            }

            public Dictionary<string, DeclaredPackage> Versions { get; }

            public IReadOnlyList<DeclaredPackage> GlobalReferences { get; }

            public static PackagesProps Read(string relativePath, XDocument document)
            {
                Dictionary<string, DeclaredPackage> versions = new Dictionary<string, DeclaredPackage>(StringComparer.OrdinalIgnoreCase);
                foreach (DeclaredPackage package in Packages(relativePath, document, "PackageVersion"))
                {
                    // The first one wins; NuGet rejects a package whose version is set twice anyway
                    versions.TryAdd(package.PackageId, package);
                }

                return new PackagesProps(versions, Packages(relativePath, document, "GlobalPackageReference").ToList());
            }

            private static IEnumerable<DeclaredPackage> Packages(string relativePath, XDocument document, string itemType)
            {
                foreach (XElement item in Items(document, itemType))
                {
                    string? packageId = ((string?)item.Attribute("Include"))?.Trim();
                    string? version = Metadata(item, "Version");

                    if (!string.IsNullOrEmpty(packageId) && version != null)
                    {
                        yield return new DeclaredPackage(relativePath, LineOf(item), packageId, version);
                    }
                }
            }
        }
    }
}
