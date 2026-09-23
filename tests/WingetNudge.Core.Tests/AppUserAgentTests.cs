using System.Net.Http.Headers;
using System.Reflection;
using System.Reflection.Emit;
using System.Xml.Linq;
using AwesomeAssertions;
using WingetNudge.Core.Tracking;

namespace WingetNudge.Core.Tests;

public sealed class AppUserAgentTests
{
    [Fact]
    public void For_CarriesTheVersionDirectoryBuildPropsSets()
    {
        string root =
            typeof(AppUserAgentTests)
                .Assembly.GetCustomAttributes<AssemblyMetadataAttribute>()
                .SingleOrDefault(attribute => attribute.Key == "RepositoryRoot")
                ?.Value
            ?? throw new InvalidOperationException("The test assembly carries no RepositoryRoot metadata.");
        string propsPath = Path.Combine(root, "Directory.Build.props");
        string released =
            XDocument.Load(propsPath).Root?.Elements("PropertyGroup").Elements("Version").SingleOrDefault()?.Value
            ?? throw new InvalidDataException($"{propsPath} sets no Version.");

        ProductInfoHeaderValue token = AppUserAgent.For(typeof(AppUserAgent).Assembly);

        token
            .ToString()
            .Should()
            .Be($"WingetNudge/{released}", "release-please writes the app's version into {0}", propsPath);
    }

    // A built assembly carries build metadata only when it comes from a git checkout. These cases choose the
    // informational version, so the cut is pinned either way.
    [Theory]
    [InlineData("1.0.0+2a28da9b5d5855ee316d118d053e3e409d61fca0", "WingetNudge/1.0.0")]
    [InlineData("1.0.0-rc.1+2a28da9b5d5855ee316d118d053e3e409d61fca0", "WingetNudge/1.0.0-rc.1")]
    [InlineData("1.0.0", "WingetNudge/1.0.0")]
    public void For_CutsBuildMetadataAndKeepsThePrereleaseLabel(string informationalVersion, string expected)
    {
        ConstructorInfo constructor =
            typeof(AssemblyInformationalVersionAttribute).GetConstructor([typeof(string)])
            ?? throw new MissingMethodException(nameof(AssemblyInformationalVersionAttribute), ".ctor(string)");
        Assembly assembly = AssemblyBuilder.DefineDynamicAssembly(
            new AssemblyName("InformationalVersionCase"),
            AssemblyBuilderAccess.Run,
            [new CustomAttributeBuilder(constructor, [informationalVersion])]
        );

        AppUserAgent.For(assembly).ToString().Should().Be(expected);
    }

    [Fact]
    public void For_ThrowsNamingTheAssemblyWhenItCarriesNoInformationalVersion()
    {
        Assembly assembly = AssemblyBuilder.DefineDynamicAssembly(
            new AssemblyName("NoInformationalVersion"),
            AssemblyBuilderAccess.Run
        );

        Action build = () => AppUserAgent.For(assembly);

        build
            .Should()
            .Throw<InvalidOperationException>()
            .WithMessage("NoInformationalVersion carries no informational version.");
    }
}
