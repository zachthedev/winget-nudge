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
/// Directory.Packages.props or Directory.Build.props from. Each case binds one restated version to the
/// pin it derives from.
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

        FoldedText(Path.Combine(root, document))
            .Contains(expected, StringComparison.Ordinal)
            .Should()
            .BeTrue(
                "{0} must say \"{1}\" while Directory.Packages.props pins {2} at {3}",
                document,
                expected,
                pin,
                pinned
            );
    }

    [Theory]
    // SupportedOSPlatformVersion is the floor setup refuses below, and TargetPlatformMinVersion the lowest
    // Windows the app declares. Either one below Windows 11's first build admits Windows 10.
    [InlineData("SupportedOSPlatformVersion")]
    [InlineData("TargetPlatformMinVersion")]
    public void Document_NamesWindows11_WhileTheFloorIsAWindows11Build(string property)
    {
        const int Windows11FirstBuild = 22000;
        string root = Metadata("RepositoryRoot");
        string propsPath = Path.Combine(root, "Directory.Build.props");
        XElement project =
            XDocument.Load(propsPath).Root ?? throw new InvalidDataException($"{propsPath} has no root element.");
        string? floor = project.Elements("PropertyGroup").Elements(property).SingleOrDefault()?.Value;
        Version version = Version.TryParse(floor, out Version? parsed)
            ? parsed
            : throw new InvalidDataException(
                $"Directory.Build.props sets {property} to \"{floor}\", which is not a version."
            );

        version
            .Build.Should()
            .BeGreaterThanOrEqualTo(
                Windows11FirstBuild,
                "docs/install.md names Windows 11, and Directory.Build.props sets {0} to {1}",
                property,
                floor
            );
        FoldedText(Path.Combine(root, "docs/install.md"))
            .Contains("Windows 11 on x64", StringComparison.Ordinal)
            .Should()
            .BeTrue(
                "docs/install.md's Requirements line must say \"Windows 11 on x64\" while the build floor admits Windows 11 alone"
            );
    }

    // Markdown wraps prose at any space, so a document's whitespace folds to single spaces before a phrase is sought.
    private static string FoldedText(string path) =>
        string.Join(' ', File.ReadAllText(path).Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries));

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
