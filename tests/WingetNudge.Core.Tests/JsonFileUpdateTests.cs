using System.Collections.Concurrent;
using AwesomeAssertions;
using Microsoft.Win32.SafeHandles;
using WingetNudge.Core.Storage;
using WingetNudge.Core.Tests.Support;

namespace WingetNudge.Core.Tests;

// The write lock is an exclusive handle on a file beside the data file. Share modes act per handle
// rather than per process, so a second handle or a second thread here stands in for a second process.
public sealed class JsonFileUpdateTests : IDisposable
{
    private static readonly Dictionary<string, string> Old = new(StringComparer.Ordinal) { ["value"] = "old" };
    private static readonly Dictionary<string, string> New = new(StringComparer.Ordinal) { ["value"] = "new" };

    // ERROR_DIR_NOT_EMPTY, as an IOException carries it.
    private const int DirectoryNotEmpty = unchecked((int)0x80070091);

    private readonly TempData _data = new();

    private string DataFile => _data.Paths.Preferences;

    public void Dispose() => _data.Dispose();

    [Fact]
    public void Update_HandsTheChangeWhatIsOnDiskNow()
    {
        JsonFile.Update<Dictionary<string, string>>(DataFile, false, current => With(current, "mine", "1"));
        JsonFile.Write(
            DataFile,
            new Dictionary<string, string>(StringComparer.Ordinal) { ["mine"] = "1", ["theirs"] = "2" }
        );

        Dictionary<string, string>? seen = null;
        JsonFile.Update<Dictionary<string, string>>(
            DataFile,
            false,
            current =>
            {
                seen = current is null ? null : new Dictionary<string, string>(current, StringComparer.Ordinal);
                return With(current, "mine", "3");
            }
        );

        seen.Should().Equal(new Dictionary<string, string> { ["mine"] = "1", ["theirs"] = "2" });
        Read().Should().Equal(new Dictionary<string, string> { ["mine"] = "3", ["theirs"] = "2" });
    }

    [Fact(Timeout = 60_000)]
    public async Task Update_FromManyWritersAtOnce_KeepsEveryChange()
    {
        const int writers = 4;
        const int updates = 50;
        ConcurrentQueue<Exception> failures = new();

        Task[] running = Enumerable
            .Range(0, writers)
            .Select(writer =>
                Task.Run(
                    () =>
                    {
                        for (int update = 0; update < updates; update++)
                        {
                            string key = $"writer{writer}-{update}";
                            try
                            {
                                // Four writers back to back can pass one waiter over for the default
                                // two seconds on a loaded machine. The case is about lost changes, not
                                // the wait.
                                JsonFile.Update<Dictionary<string, string>>(
                                    DataFile,
                                    false,
                                    current => With(current, key, "written"),
                                    TimeSpan.FromSeconds(30)
                                );
                            }
                            catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
                            {
                                failures.Enqueue(exception);
                            }
                        }
                    },
                    TestContext.Current.CancellationToken
                )
            )
            .ToArray();
        await Task.WhenAll(running);

        failures.Should().BeEmpty("a writer that failed saved nothing, and its key would read as overwritten");
        Read().Should().HaveCount(writers * updates, "no writer's change may overwrite another's");
    }

    [Fact]
    public void Update_WhileAnotherHandleHoldsTheLock_WaitsThenApplies()
    {
        JsonFile.Write(DataFile, Old);
        FileStream holder = new(
            DataFile + JsonFile.LockSuffix,
            FileMode.OpenOrCreate,
            FileAccess.ReadWrite,
            FileShare.None
        );

        // The update runs on this thread as soon as the releaser starts, so it meets the held lock.
        Thread releaser = new(() =>
        {
            Thread.Sleep(50);
            holder.Dispose();
        });
        releaser.Start();
        Action update = () =>
            JsonFile.Update<Dictionary<string, string>>(DataFile, false, current => With(current, "value", "new"));

        try
        {
            update.Should().NotThrow("the update waits for a lock that another handle lets go");
        }
        finally
        {
            // The data directory is deleted after the case, and a held lock file would refuse that.
            releaser.Join();
        }

        Read().Should().Equal(New);
    }

    [Fact]
    public void Update_WhenTheLockStaysHeld_ThrowsIOExceptionWithoutWriting()
    {
        JsonFile.Write(DataFile, Old);
        bool changed = false;

        using (
            new FileStream(DataFile + JsonFile.LockSuffix, FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None)
        )
        {
            Action update = () =>
                JsonFile.Update<Dictionary<string, string>>(
                    DataFile,
                    false,
                    current =>
                    {
                        changed = true;
                        return With(current, "value", "new");
                    },
                    TimeSpan.FromMilliseconds(100)
                );

            update.Should().Throw<IOException>().WithMessage("*preferences.json*");
        }

        changed.Should().BeFalse("the change runs only under the lock");
        Read().Should().Equal(Old);
        Directory.GetFiles(_data.Paths.Directory, $"*{JsonFile.TemporarySuffix}").Should().BeEmpty();
    }

    [Fact]
    public void Update_CalledInsideAnotherUpdate_Throws()
    {
        string other = Path.Combine(_data.Paths.Directory, "other.json");
        Action nested = () =>
            JsonFile.Update<Dictionary<string, string>>(
                DataFile,
                false,
                current =>
                {
                    JsonFile.Update<Dictionary<string, string>>(other, false, inner => With(inner, "value", "new"));
                    return With(current, "value", "new");
                }
            );

        nested.Should().Throw<InvalidOperationException>("a thread holds one write lock at a time");
        File.Exists(other).Should().BeFalse("the inner update never ran");
        File.Exists(DataFile).Should().BeFalse("the outer update failed with it");
    }

    [Fact]
    public void Update_WhenTheChangeThrows_ReleasesTheLockAndKeepsTheFile()
    {
        JsonFile.Write(DataFile, Old);
        Action failing = () =>
            JsonFile.Update<Dictionary<string, string>>(
                DataFile,
                false,
                _ => throw new InvalidOperationException("the change failed")
            );

        failing.Should().Throw<InvalidOperationException>().WithMessage("the change failed");
        Read().Should().Equal(Old);

        Action next = () =>
            JsonFile.Update<Dictionary<string, string>>(
                DataFile,
                false,
                current => With(current, "value", "new"),
                TimeSpan.FromMilliseconds(100)
            );
        next.Should().NotThrow("the failed update released the lock");
        Read().Should().Equal(New);
    }

    [Fact]
    public void Update_ThroughALinkedDataDirectory_Refuses()
    {
        string real = Directory.CreateDirectory(Path.Combine(_data.Root, "elsewhere")).FullName;
        string link = Path.Combine(_data.Root, "linked-data");
        Directory.CreateSymbolicLink(link, real);

        try
        {
            Action update = () =>
                JsonFile.Update<Dictionary<string, string>>(
                    Path.Combine(link, "preferences.json"),
                    false,
                    current => With(current, "value", "new")
                );

            update.Should().Throw<IOException>();
            Directory.GetFiles(real).Should().BeEmpty("neither the lock file nor the data may land behind the link");
        }
        finally
        {
            Directory.Delete(link);
        }
    }

    [Fact]
    public void Update_AndRunLock_WhenTheLockFileIsALink_RefuseWithoutCreatingItsTarget()
    {
        Directory.CreateDirectory(_data.Paths.Directory);
        string outside = Directory.CreateDirectory(Path.Combine(_data.Root, "outside")).FullName;
        File.CreateSymbolicLink(DataFile + JsonFile.LockSuffix, Path.Combine(outside, "through-data-lock"));
        File.CreateSymbolicLink(_data.Paths.RunLockFile(RunLock.Check), Path.Combine(outside, "through-run-lock"));

        Action update = () =>
            JsonFile.Update<Dictionary<string, string>>(DataFile, false, current => With(current, "value", "new"));

        update.Should().Throw<IOException>().WithMessage($"*{SafePath.ReparsePointCause}*");
        RunLock
            .Acquire(_data.Paths, RunLock.Check)
            .Should()
            .BeOfType<RunLockAttempt.Unavailable>("a link at a lock file is refused rather than followed");
        Directory.GetFiles(outside).Should().BeEmpty("no lock open may create the file a planted link names");
        File.Exists(DataFile).Should().BeFalse("an update that took no lock writes nothing");
    }

    [Fact]
    public void Update_AndRunLock_WhenTheLockPathIsADirectoryLink_RefuseItAsALink()
    {
        Directory.CreateDirectory(_data.Paths.Directory);
        string outside = Directory.CreateDirectory(Path.Combine(_data.Root, "outside")).FullName;
        string existing = Directory.CreateDirectory(Path.Combine(outside, "existing")).FullName;
        string missing = Path.Combine(outside, "missing");
        Directory.CreateSymbolicLink(DataFile + JsonFile.LockSuffix, existing);
        Directory.CreateSymbolicLink(_data.Paths.RunLockFile(RunLock.Check), missing);

        Action update = () =>
            JsonFile.Update<Dictionary<string, string>>(DataFile, false, current => With(current, "value", "new"));

        update
            .Should()
            .Throw<IOException>()
            .WithMessage($"*{SafePath.ReparsePointCause}*", "diagnostics.log names the link rather than a denied open");
        RunLock
            .Acquire(_data.Paths, RunLock.Check)
            .Should()
            .BeOfType<RunLockAttempt.Unavailable>()
            .Which.Reason.Should()
            .Contain(SafePath.ReparsePointCause);
        Directory.GetFileSystemEntries(existing).Should().BeEmpty("nothing may land where the link points");
        Path.Exists(missing).Should().BeFalse("a dangling link's target is never created");
        File.Exists(DataFile).Should().BeFalse("an update that took no lock writes nothing");
    }

    [Fact]
    public void Update_WhenTheDataDirectoryBecomesAJunctionAfterItsCheck_RefusesWithoutWritingThroughIt()
    {
        string elsewhere = Directory.CreateDirectory(Path.Combine(_data.Root, "elsewhere")).FullName;
        bool changed = false;
        Action update = () =>
            JsonFile.Update<Dictionary<string, string>>(
                DataFile,
                false,
                current =>
                {
                    changed = true;
                    return With(current, "value", "new");
                }
            );

        using (JunctionAfterCheck swap = new(_data.Paths.Directory, elsewhere))
        {
            update
                .Should()
                .Throw<IOException>("a directory that became a link after its check refuses the lock's open")
                .WithMessage(
                    $"*'{_data.Paths.Directory}' {SafePath.ReparsePointCause}*",
                    "diagnostics.log names the directory and the link as the cause"
                );
            swap.Converted.Should().BeTrue("the empty data directory became a junction once its check passed");
        }

        Directory
            .EnumerateFileSystemEntries(elsewhere)
            .Should()
            .BeEmpty("neither the lock file nor the data lands where the junction points");
        changed.Should().BeFalse("the change runs only under the lock");
    }

    [Fact]
    public void Update_WhenAnAncestorIsRenamedMidWalk_RefusesTheRenameAndWritesInTheRealDirectory()
    {
        string ancestor = Path.Combine(_data.Root, "a");
        string file = Path.Combine(ancestor, "data", "preferences.json");
        string elsewhere = Directory.CreateDirectory(Path.Combine(_data.Root, "elsewhere")).FullName;
        Exception? renaming = new InvalidOperationException("the walk never reached the ancestor");
        Action<string>? previous = SafePath.BetweenSteps.Value;

        // Once the walk verifies the ancestor, another process running as the user renames it away and plants a junction
        // at its old name. The handles still name the real directory, but a write by path would land in the target.
        SafePath.BetweenSteps.Value = verified =>
        {
            if (string.Equals(verified, ancestor, StringComparison.OrdinalIgnoreCase))
            {
                renaming = Record.Exception(() => Directory.Move(ancestor, ancestor + "-moved"));
                if (renaming is null)
                {
                    Junction.Create(ancestor, elsewhere);
                }
            }
        };
        Action update = () =>
            JsonFile.Update<Dictionary<string, string>>(file, false, current => With(current, "value", "new"));

        try
        {
            update.Should().NotThrow("the ancestor stays where the walk verified it");
        }
        finally
        {
            SafePath.BetweenSteps.Value = previous;
            if (SafePath.IsReparsePoint(ancestor))
            {
                Directory.Delete(ancestor);
            }
        }

        Directory
            .EnumerateFileSystemEntries(elsewhere)
            .Should()
            .BeEmpty("the write never lands where a junction points");
        JsonFile
            .Read<Dictionary<string, string>>(file, deleteIfCorrupt: false)
            .Should()
            .Equal(New, "the write lands in the real directory");
        renaming
            .Should()
            .BeOfType<IOException>("the walk holds every component it opened without delete sharing")
            .Which.HResult.Should()
            .Be(ExclusiveFile.SharingViolation);
    }

    [Fact]
    public void Update_WhileAnotherProgramHoldsAnAncestorWithDeleteAccess_WaitsThenSaves()
    {
        string ancestor = Directory.CreateDirectory(Path.Combine(_data.Root, "a")).FullName;
        string file = Path.Combine(ancestor, "data", "preferences.json");
        SafeFileHandle holder = DeleteAccessHandle.Open(ancestor);

        // The walk meets the holder at its first attempt and again after the first wait. The holder lets go at the
        // second wait, so the third attempt walks through.
        using AttemptWaits waits = new(2, holder.Dispose);
        Action update = () =>
            JsonFile.Update<Dictionary<string, string>>(file, false, current => With(current, "value", "new"));

        try
        {
            update.Should().NotThrow("a walk another program holds up waits for it, as a held lock does");
        }
        finally
        {
            holder.Dispose();
        }

        JsonFile
            .Read<Dictionary<string, string>>(file, deleteIfCorrupt: false)
            .Should()
            .Equal(New, "the save lands once the holder lets go");
        waits.Delays.Should().Equal([1, 1], "the save waited out the holder at the lock loop's waits");
    }

    [Fact]
    public void Write_UnderTheLock_StaysInTheDataDirectoryWhileAnotherProcessTriesToLinkIt()
    {
        string elsewhere = Directory.CreateDirectory(Path.Combine(_data.Root, "elsewhere")).FullName;
        Exception? deleting = null;
        Exception? linking = null;

        // Inside the change the lock is held and the data directory holds the lock file alone. Another process running
        // as the user empties the directory and makes it a junction, so a write by path would land in the target.
        Action update = () =>
            JsonFile.Update<Dictionary<string, string>>(
                DataFile,
                false,
                current =>
                {
                    deleting = Record.Exception(() => File.Delete(DataFile + JsonFile.LockSuffix));
                    linking = Record.Exception(() => Junction.Create(_data.Paths.Directory, elsewhere));
                    return With(current, "value", "new");
                }
            );

        try
        {
            update.Should().NotThrow("the directory the write goes to is the one the lock's open verified");
        }
        finally
        {
            if (SafePath.IsReparsePoint(_data.Paths.Directory))
            {
                Directory.Delete(_data.Paths.Directory);
            }
        }

        Directory
            .EnumerateFileSystemEntries(elsewhere)
            .Should()
            .BeEmpty("the write never lands where a junction points");
        Read().Should().Equal(New, "the write lands in the data directory");
        deleting
            .Should()
            .BeOfType<IOException>("the lock file shares no delete")
            .Which.HResult.Should()
            .Be(ExclusiveFile.SharingViolation);
        linking
            .Should()
            .BeOfType<IOException>("the lock file keeps the directory from being emptied")
            .Which.HResult.Should()
            .Be(DirectoryNotEmpty);
    }

    [Fact]
    public void SetAside_UnderTheLock_StaysInTheDataDirectoryWhileAnotherProcessTriesToLinkIt()
    {
        Directory.CreateDirectory(_data.Paths.Directory);
        File.WriteAllText(DataFile, "{ corrupt");
        string elsewhere = Directory.CreateDirectory(Path.Combine(_data.Root, "elsewhere")).FullName;
        string victim = Path.Combine(elsewhere, Path.GetFileName(DataFile));
        File.WriteAllText(victim, "victim");
        Exception? deleting = null;
        Exception? linking = null;
        Exception? refused = new InvalidOperationException("the set-aside never ran");

        // Under the lock, another process running as the user moves the corrupt file out, then empties the directory and
        // makes it a junction to a directory holding a file of the same name. A set-aside by path would then move that
        // file.
        Action setAside = () =>
            JsonFile.Locked(
                DataFile,
                () =>
                {
                    File.Move(DataFile, Path.Combine(_data.Root, "stash.json"));
                    deleting = Record.Exception(() => File.Delete(DataFile + JsonFile.LockSuffix));
                    linking = Record.Exception(() => Junction.Create(_data.Paths.Directory, elsewhere));
                    refused = JsonFile.SetAside(DataFile, delete: false, beforeWrite: true);
                    return true;
                }
            );

        try
        {
            setAside.Should().NotThrow("the directory the set-aside acts in is the one the lock's open verified");
        }
        finally
        {
            if (SafePath.IsReparsePoint(_data.Paths.Directory))
            {
                Directory.Delete(_data.Paths.Directory);
            }
        }

        Directory
            .EnumerateFileSystemEntries(elsewhere)
            .Should()
            .Equal([victim], "no move or delete reaches the directory a junction would point to");
        File.ReadAllText(victim).Should().Be("victim");
        refused.Should().BeNull("nothing stands at the path once the corrupt file moved out");
        deleting
            .Should()
            .BeOfType<IOException>("the lock file shares no delete")
            .Which.HResult.Should()
            .Be(ExclusiveFile.SharingViolation);
        linking
            .Should()
            .BeOfType<IOException>("the lock file keeps the directory from being emptied")
            .Which.HResult.Should()
            .Be(DirectoryNotEmpty);
    }

    [Fact]
    public void Read_OfACorruptFileAnotherHandleHolds_ReturnsWithoutWaitingOnTheHolder()
    {
        // A scanner or another program's reader holding the corrupt file without delete sharing refuses the
        // move. A read leaves the file for the next writer rather than waiting the holder out, so the
        // picker's reload never stalls.
        Directory.CreateDirectory(_data.Paths.Directory);
        File.WriteAllText(DataFile, "{ corrupt");
        using FileStream holder = new(DataFile, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
        using AttemptWaits waits = new();

        JsonFile.Read<Dictionary<string, string>>(DataFile, deleteIfCorrupt: false).Should().BeNull();
        JsonFile.Read<Dictionary<string, string>>(DataFile, deleteIfCorrupt: false).Should().BeNull();

        waits.Delays.Should().BeEmpty("a read tries the move once and never waits out the holder");
        File.Exists(DataFile).Should().BeTrue("the held file stays for the next writer");
    }

    [Fact]
    public void Update_WhileAReaderHoldsTheTarget_Lands()
    {
        JsonFile.Write(DataFile, Old);
        FileStream reader = new(DataFile, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);

        // The update's replace meets the held target, and the reader lets go at the wait that follows. An update
        // whose replace does not wait meets the reader at its only attempt.
        using AttemptWaits waits = new(1, reader.Dispose);
        Action update = () =>
            JsonFile.Update<Dictionary<string, string>>(DataFile, false, current => With(current, "value", "new"));

        try
        {
            update.Should().NotThrow("the update waits out a reader that closes");
        }
        finally
        {
            // The data directory is deleted after the case, and a held file would refuse that.
            reader.Dispose();
        }

        Read().Should().Equal(New);
    }

    private static Dictionary<string, string> With(Dictionary<string, string>? current, string key, string value)
    {
        Dictionary<string, string> next = current ?? new Dictionary<string, string>(StringComparer.Ordinal);
        next[key] = value;
        return next;
    }

    private Dictionary<string, string>? Read() =>
        JsonFile.Read<Dictionary<string, string>>(DataFile, deleteIfCorrupt: false);
}
