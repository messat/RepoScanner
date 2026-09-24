using System;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace RepoScanner.Data.Configurations
{
    internal static class EnumPropertyBuilderExtensions
    {
        // Stores an enum as its name ('Open', not 0), so the tables read clearly in SSMS and reordering the
        // enum's values in C# cannot change what stored rows mean. varchar(20): the names are ASCII and the
        // longest (PartiallyCompleted, SuppressionExpired) is 18 characters.
        public static PropertyBuilder<TEnum> IsStoredAsText<TEnum>(this PropertyBuilder<TEnum> property)
            where TEnum : struct, Enum
        {
            return property
                .HasConversion<string>()
                .HasMaxLength(20)
                .IsUnicode(false)
                .IsRequired();
        }
    }
}
