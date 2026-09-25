using System.Collections.Concurrent;
using System.Diagnostics;
using AwesomeAssertions;
using WingetNudge.Core.Storage;
using WingetNudge.Core.Tests.Support;

namespace WingetNudge.Core.Tests;

// The write lock is an exclusive handle on a file beside the data file. Share modes act per handle
// rather than per process, so a second handle or a second thread here stands in for a second process.
public sealed class JsonFileUpdateTests : IDisposable
{
    private static readonly Dictionary<string, string> Old = new(StringComparer.Ordinal) { ["value"] = "old" };
    private static readonly Dictionary<string, string> New = new(StringComparer.Ordinal) { ["value"] = "new" };

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

    [Fact(Timeout = 10_000)]
    public async Task Update_WhileAnotherHandleHoldsTheLock_WaitsThenApplies()
    {
        JsonFile.Write(DataFile, Old);
        FileStream holder = new(
            DataFile + JsonFile.LockSuffix,
            FileMode.OpenOrCreate,
            FileAccess.ReadWrite,
            FileShare.None
        );

        // A dedicated thread starts at once, so the update meets the held lock.
        Task update = Task.Factory.StartNew(
            () =>
                JsonFile.Update<Dictionary<string, string>>(DataFile, false, current => With(current, "value", "new")),
            TestContext.Current.CancellationToken,
            TaskCreationOptions.LongRunning,
            TaskScheduler.Default
        );
        await Task.Delay(50, TestContext.Current.CancellationToken);
        update.IsCompleted.Should().BeFalse("the update waits for the lock");
        holder.Dispose();

        await update.WaitAsync(TimeSpan.FromSeconds(5), TestContext.Current.CancellationToken);
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

        update.Should().Throw<IOException>().WithMessage("*reparse point*");
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
            .WithMessage("*reparse point*", "diagnostics.log names the link rather than a denied open");
        RunLock
            .Acquire(_data.Paths, RunLock.Check)
            .Should()
            .BeOfType<RunLockAttempt.Unavailable>()
            .Which.Reason.Should()
            .Contain("reparse point");
        Directory.GetFileSystemEntries(existing).Should().BeEmpty("nothing may land where the link points");
        Path.Exists(missing).Should().BeFalse("a dangling link's target is never created");
        File.Exists(DataFile).Should().BeFalse("an update that took no lock writes nothing");
    }

    [Fact]
    public void Read_OfACorruptFileAnotherHandleHolds_ReturnsWithoutWaitingOnTheHolder()
    {
        // A scanner or a second reader holding the corrupt file refuses the move. A read leaves the file
        // for the next writer rather than waiting the holder out, so the picker's reload never stalls.
        Directory.CreateDirectory(_data.Paths.Directory);
        File.WriteAllText(DataFile, "{ corrupt");
        using FileStream holder = new(DataFile, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);

        Stopwatch watch = Stopwatch.StartNew();
        JsonFile.Read<Dictionary<string, string>>(DataFile, deleteIfCorrupt: false).Should().BeNull();
        JsonFile.Read<Dictionary<string, string>>(DataFile, deleteIfCorrupt: false).Should().BeNull();
        watch.Stop();

        watch
            .ElapsedMilliseconds.Should()
            .BeLessThan(
                1_000,
                "a move that waits out a holder sleeps 511 ms or more, so two reads that wait sleep 1,022 ms or more"
            );
        File.Exists(DataFile).Should().BeTrue("the held file stays for the next writer");
    }

    [Fact]
    public void Update_WhileAReaderHoldsTheTarget_Lands()
    {
        JsonFile.Write(DataFile, Old);
        FileStream reader = new(DataFile, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
        Thread releaser = new(() =>
        {
            Thread.Sleep(50);
            reader.Dispose();
        });
        releaser.Start();

        JsonFile.Update<Dictionary<string, string>>(DataFile, false, current => With(current, "value", "new"));

        releaser.Join();
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
