using System.Diagnostics;
using System.Globalization;
using System.Reflection;
using System.Xml.Linq;
using AwesomeAssertions;

namespace WingetNudge.Core.Tests;

/// <summary>Where a version sits in Directory.Packages.props.</summary>
public enum PinKind
{
    /// <summary>An element under a PropertyGroup, named by the pin.</summary>
    Property,

    /// <summary>The Version attribute of the PackageVersion item the pin names.</summary>
    Package,
}

/// <summary>
/// docs/install.md restates the versions a user needs, because a user has no clone to read
/// Directory.Packages.props from. Each case binds one restated version to the pin it derives from.
/// </summary>
public sealed class RequirementsTests
{
    [Fact]
    public void Build_ImportsTheDirectoryPackagesPropsTheTestReads()
    {
        string read = Path.Combine(Metadata("RepositoryRoot"), "Directory.Packages.props");
        string imported = Metadata("DirectoryPackagesPropsPath");

        Path.GetFullPath(imported)
            .Should()
            .BeEquivalentTo(Path.GetFullPath(read), "the versions the build pins are the versions this test binds");
    }

    [Theory]
    // The app runs no version check against winget, so the floor is the projection's release at major.minor.
    [InlineData("docs/install.md", "winget {0} or newer", PinKind.Property, "WinGetVersion", 2)]
    // The bootstrapper refuses a runtime older than the SDK version. Each major is its own framework family.
    [InlineData(
        "docs/install.md",
        "Windows App Runtime {0} or a newer {1}.x",
        PinKind.Package,
        "Microsoft.WindowsAppSDK",
        3
    )]
    public void Document_NamesTheVersionItsPinDerivesFrom(
        string document,
        string template,
        PinKind kind,
        string pin,
        int fieldCount
    )
    {
        string root = Metadata("RepositoryRoot");
        string propsPath = Path.Combine(root, "Directory.Packages.props");
        XElement project =
            XDocument.Load(propsPath).Root ?? throw new InvalidDataException($"{propsPath} has no root element.");
        string? pinned = kind switch
        {
            PinKind.Property => project.Elements("PropertyGroup").Elements(pin).SingleOrDefault()?.Value,
            PinKind.Package => project
                .Elements("ItemGroup")
                .Elements("PackageVersion")
                .SingleOrDefault(item => (string?)item.Attribute("Include") == pin)
                ?.Attribute("Version")
                ?.Value,
            _ => throw new UnreachableException(),
        };
        Version version = Version.TryParse(pinned, out Version? parsed)
            ? parsed
            : throw new InvalidDataException(
                $"Directory.Packages.props pins {pin} at \"{pinned}\", which is not a version."
            );
        string expected = string.Format(
            CultureInfo.InvariantCulture,
            template,
            version.ToString(fieldCount),
            version.Major
        );

        // Markdown wraps prose at any space, so the document's whitespace folds to single spaces first.
        string text = string.Join(
            ' ',
            File.ReadAllText(Path.Combine(root, document)).Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries)
        );

        text.Contains(expected, StringComparison.Ordinal)
            .Should()
            .BeTrue(
                "{0} must say \"{1}\" while Directory.Packages.props pins {2} at {3}",
                document,
                expected,
                pin,
                pinned
            );
    }

    private static string Metadata(string key)
    {
        string? value = typeof(RequirementsTests)
            .Assembly.GetCustomAttributes<AssemblyMetadataAttribute>()
            .SingleOrDefault(attribute => attribute.Key == key)
            ?.Value;
        return string.IsNullOrEmpty(value)
            ? throw new InvalidOperationException($"The test assembly carries no {key} metadata.")
            : value;
    }
}
