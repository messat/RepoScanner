# Publishes the package to the internal feed. Fake values for RepoScanner's tests.
$feed = "https://build-agent:FAKE-feed-pa55@pkgs.example.com/nuget/v3/index.json"
dotnet nuget push ./out/*.nupkg --source $feed --api-key oy2fakenugetapikeyforreposcannertests000000000
