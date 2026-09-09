using AwesomeAssertions;
using Microsoft.Data.Sqlite;
using WingetNudge.Core.Packages;

namespace WingetNudge.Core.Tests;

public sealed class WingetPinReaderTests : IDisposable
{
    private readonly string _directory = Directory.CreateTempSubdirectory("winget-pins").FullName;

    public void Dispose() => Directory.Delete(_directory, recursive: true);

    // Winget's own schema, as `pinning.db` carries it.
    private string WriteDatabase(params (string Id, long Type, string Version)[] pins)
    {
        string path = Path.Combine(_directory, "pinning.db");
        using SqliteConnection connection = new($"Data Source={path};Pooling=False");
        connection.Open();
        using (SqliteCommand create = connection.CreateCommand())
        {
            create.CommandText =
                "CREATE TABLE [pin]([package_id] TEXT NOT NULL, [source_id] TEXT NOT NULL, "
                + "[type] INT64 NOT NULL, [version] TEXT NOT NULL)";
            create.ExecuteNonQuery();
        }

        foreach ((string id, long type, string version) in pins)
        {
            using SqliteCommand insert = connection.CreateCommand();
            insert.CommandText =
                "INSERT INTO pin (package_id, source_id, type, version) VALUES ($id, 'winget', $type, $version)";
            insert.Parameters.AddWithValue("$id", id);
            insert.Parameters.AddWithValue("$type", type);
            insert.Parameters.AddWithValue("$version", version);
            insert.ExecuteNonQuery();
        }

        return path;
    }

    [Theory]
    [InlineData(1, PinType.PinnedByManifest)]
    [InlineData(2, PinType.Pinning)]
    [InlineData(3, PinType.Gating)]
    [InlineData(4, PinType.Blocking)]
    [InlineData(99, PinType.None)]
    public void Load_MapsWingetsStoredPinTypes(long stored, PinType expected)
    {
        string path = WriteDatabase(("Some.Package", stored, ""));

        IReadOnlyDictionary<string, WingetPin> pins = new WingetPinReader(path).Load();

        pins["Some.Package"].Type.Should().Be(expected);
    }

    [Fact]
    public void Load_KeepsTheStrictestPinWhenAPackageIsPinnedInTwoSources()
    {
        string path = WriteDatabase(("Adobe.Acrobat.Pro", 2, ""), ("Adobe.Acrobat.Pro", 4, ""));

        IReadOnlyDictionary<string, WingetPin> pins = new WingetPinReader(path).Load();

        pins["Adobe.Acrobat.Pro"].Type.Should().Be(PinType.Blocking);
        pins["Adobe.Acrobat.Pro"].Blocks.Should().BeTrue();
    }

    [Fact]
    public void Load_MatchesPackageIdsWithoutRegardToCase()
    {
        string path = WriteDatabase(("Parsec.Parsec", 2, ""));

        new WingetPinReader(path).Load().ContainsKey("parsec.parsec").Should().BeTrue();
    }

    [Fact]
    public void Load_SurvivesANullColumn()
    {
        string path = Path.Combine(_directory, "pinning.db");
        using (SqliteConnection connection = new($"Data Source={path};Pooling=False"))
        {
            connection.Open();
            using SqliteCommand command = connection.CreateCommand();
            command.CommandText =
                "CREATE TABLE [pin]([package_id] TEXT, [source_id] TEXT, [type] INT64, [version] TEXT);"
                + "INSERT INTO pin VALUES ('Good.Package', 'winget', 4, NULL);"
                + "INSERT INTO pin VALUES (NULL, 'winget', 2, '1.0')";
            command.ExecuteNonQuery();
        }

        IReadOnlyDictionary<string, WingetPin> pins = new WingetPinReader(path).Load();

        pins["Good.Package"].Type.Should().Be(PinType.Blocking);
        pins["Good.Package"].Version.Should().BeEmpty();
        pins.Should().HaveCount(1, "a row with no package id names nothing");
    }

    [Fact]
    public void Load_ReturnsNothingWhenTheDatabaseIsAbsent()
    {
        new WingetPinReader(Path.Combine(_directory, "missing.db")).Load().Should().BeEmpty();
    }

    [Fact]
    public void Load_ReturnsNothingWhenTheFileIsNotADatabase()
    {
        string path = Path.Combine(_directory, "pinning.db");
        File.WriteAllText(path, "this is not sqlite");

        new WingetPinReader(path).Load().Should().BeEmpty("an unreadable pin file is not a crash");
    }

    [Fact]
    public void Describe_NamesWhatEachPinDoes()
    {
        new WingetPin("a", PinType.Blocking, "").Describe().Should().Contain("blocked");
        new WingetPin("a", PinType.Gating, "1.2.*").Describe().Should().Contain("1.2.*");
        new WingetPin("a", PinType.Pinning, "").Describe().Should().Contain("bulk upgrades");
        new WingetPin("a", PinType.Blocking, "").Blocks.Should().BeTrue();
        new WingetPin("a", PinType.Pinning, "").Blocks.Should().BeFalse();
    }
}

public sealed class PackageInfoHeldBackTests
{
    private static PackageInfo Package(string installed, string offered, bool updateAvailable) =>
        new("Some.Package", "Some Package", installed, offered, updateAvailable)
        {
            OfferedVersion = offered,
        };

    [Fact]
    public void IsHeldBack_IsTrueWhenWingetNamesANewerVersionButOffersNoUpgrade()
    {
        Package("1.7.2", "1.19.2", false).IsHeldBack.Should().BeTrue();
    }

    [Fact]
    public void IsHeldBack_IsFalseWhenTheUpgradeIsOnOffer()
    {
        Package("1.7.2", "1.19.2", true).IsHeldBack.Should().BeFalse();
    }

    [Theory]
    [InlineData("2.2.0.0", "2.2.0")]
    [InlineData("1.27.1", "1.27.0")]
    [InlineData("ad 9.7.15", "9.7.15")]
    public void IsHeldBack_IsFalseWhenTheCatalogIsNotAhead(string installed, string offered)
    {
        Package(installed, offered, false).IsHeldBack.Should().BeFalse();
    }

    [Fact]
    public void IsHeldBack_IsFalseWithNoOfferedVersion()
    {
        new PackageInfo("Some.Package", "Some Package", "1.0", "1.0", false)
            .IsHeldBack.Should()
            .BeFalse();
    }
}
