using System.CommandLine;
using System.ComponentModel;
using System.Globalization;
using Humanizer;
using Spectre.Console;
using WingetNudge.Core.Packages;
using WingetNudge.Core.Registration;
using WingetNudge.Core.Storage;
using WingetNudge.Core.Tools;
using WingetNudge.Core.Tracking;
using WingetNudge.Services;

namespace WingetNudge.Cli;

/// <summary>Command-line verbs. GUI verbs hand a <see cref="StartupMode"/> back; the rest print.</summary>
public static class Commands
{
    /// <summary>Builds the command tree.</summary>
    /// <param name="openWindow">Receives the window a GUI verb wants opened.</param>
    /// <returns>The root command.</returns>
    public static RootCommand Build(Action<StartupMode> openWindow)
    {
        RootCommand root = new(
            "Checks winget for package upgrades and nudges you with a notification."
        );
        root.SetAction(_ => openWindow(new StartupMode.Picker()));

        root.Subcommands.Add(BuildPicker(openWindow));
        root.Subcommands.Add(BuildUpgrade(openWindow));
        root.Subcommands.Add(BuildCheck());
        root.Subcommands.Add(BuildList());
        root.Subcommands.Add(BuildUpdateAll());
        root.Subcommands.Add(BuildRegister());
        root.Subcommands.Add(BuildUnregister());
        root.Subcommands.Add(BuildTool());
        root.Subcommands.Add(BuildTracking());
        return root;
    }

    // ///// GUI verbs /////

    private static Command BuildPicker(Action<StartupMode> openWindow)
    {
        Command command = new(Launcher.PickerVerb, "Open the package picker.");
        command.SetAction(_ => openWindow(new StartupMode.Picker()));
        return command;
    }

    private static Command BuildUpgrade(Action<StartupMode> openWindow)
    {
        Option<string[]> ids = new("--id")
        {
            Description = "Package id to upgrade. Repeatable.",
            Required = true,
        };
        Option<string[]> names = new("--name")
        {
            Description = "Display name matching each --id, in order.",
        };
        Command command = new(
            Launcher.UpgradeVerb,
            "Upgrade packages in an elevated progress window."
        )
        {
            ids,
            names,
        };
        command.SetAction(result =>
        {
            string[] idValues = result.GetValue(ids) ?? [];
            string[] nameValues = result.GetValue(names) ?? [];
            List<PackageRef> packages = [];
            for (int index = 0; index < idValues.Length; index++)
            {
                PackageIdValidator.Ensure(idValues[index]);
                if (index < nameValues.Length)
                {
                    PackageIdValidator.EnsureName(nameValues[index]);
                }

                packages.Add(
                    new PackageRef(
                        idValues[index],
                        index < nameValues.Length ? nameValues[index] : ""
                    )
                );
            }

            openWindow(new StartupMode.Upgrade(packages));
        });
        return command;
    }

    // ///// Headless verbs /////

    private static Command BuildCheck()
    {
        Option<bool> background = new(StartupRegistrar.BackgroundFlag)
        {
            Description =
                "Run on the interval schedule: refresh release dates and the cooldown, and "
                + "announce only what the user has not been told about, and only when the "
                + "notify-on-new-updates setting is on.",
        };
        Command command = new(
            StartupRegistrar.CheckVerb,
            "Check for upgrades and show a notification when any exist."
        )
        {
            background,
        };
        command.SetAction(
            async (parsed, cancellationToken) =>
            {
                AppServices services = AppServices.Current;

                // The scheduled check and the quiet interval check are separate tasks, so a
                // resume that makes both due starts two full winget scans over one set of state
                // files. The second one has nothing to add, so it stands down.
                RunLockAttempt attempt = RunLock.Acquire(services.Paths, RunLock.Check);
                if (attempt is RunLockAttempt.Unavailable unavailable)
                {
                    // Task Scheduler is what runs this verb, and its history is the only place a
                    // headless failure shows, so an unusable lock exits non-zero rather than
                    // reading as a run that stood down.
                    AnsiConsole.MarkupLineInterpolated(
                        CultureInfo.InvariantCulture,
                        $"[red]\u2717[/] {unavailable.Reason}"
                    );
                    return 1;
                }

                if (attempt is not RunLockAttempt.Taken taken)
                {
                    AnsiConsole.MarkupLine("[grey]\u00B7[/] another check is already running");
                    return 0;
                }

                using RunLock run = taken.Lock;

                bool quiet = parsed.GetValue(background);
                UpdateCheckResult result = await services.UpdateCheck.RunAsync(cancellationToken);
                IReadOnlyList<string> keys = result.Keys;

                if (quiet && !services.Settings.NotifyOnNewUpdates)
                {
                    AnsiConsole.MarkupLineInterpolated(
                        CultureInfo.InvariantCulture,
                        $"[grey]·[/] refreshed, {"update".ToQuantity(keys.Count)} pending, staying quiet"
                    );
                    return 0;
                }

                // A four-hourly check would otherwise announce the same packages all day.
                if (quiet && !services.Notifications.HasNews(keys))
                {
                    AnsiConsole.MarkupLine(
                        "[grey]·[/] refreshed, nothing new since the last notice"
                    );
                    return 0;
                }

                if (UpdateNotifier.Show(result.Names))
                {
                    services.Notifications.Record(keys);
                    AnsiConsole.MarkupLineInterpolated(
                        CultureInfo.InvariantCulture,
                        $"[green]✓[/] {"update".ToQuantity(result.Names.Count)}, notification shown"
                    );
                }
                else
                {
                    AnsiConsole.MarkupLine("[grey]·[/] no updates");
                }

                return 0;
            }
        );
        return command;
    }

    private static Command BuildList()
    {
        Option<bool> everything = new("--all")
        {
            Description = "Include packages with no upgrade on offer.",
        };
        Option<string?> explain = new("--explain")
        {
            Description = "Dump what the winget COM API reports for one package id.",
        };
        Command command = new("list", "Print every section the picker would show.")
        {
            everything,
            explain,
        };
        command.SetAction(
            async (result, cancellationToken) =>
            {
                if (result.GetValue(explain) is string target)
                {
                    IReadOnlyList<string> facts = await AppServices.Current.Winget.ExplainAsync(
                        target
                    );
                    foreach (string line in facts)
                    {
                        AnsiConsole.WriteLine(line);
                    }

                    return 0;
                }

                UpdateCheckResult check = await AppServices.Current.UpdateCheck.RunAsync(
                    cancellationToken
                );
                Table table = new Table()
                    .Border(TableBorder.None)
                    .AddColumns("section", "id", "name", "installed", "available", "note");

                foreach (UpdateCandidate candidate in check.Partition.Normal)
                {
                    Row(table, "ready", candidate, "");
                }

                foreach (
                    (UpdateCandidate candidate, CoolingInfo cooling) in check.Partition.Cooling
                )
                {
                    Row(table, "too new", candidate, $"{cooling.RemainingHours}h left");
                }

                foreach (UpdateCandidate candidate in check.Partition.Skipped)
                {
                    Row(table, "skipped", candidate, "");
                }

                foreach (UpdateCandidate candidate in check.Partition.Muted)
                {
                    Row(table, "muted", candidate, "");
                }

                foreach ((UpdateCandidate candidate, string reason) in check.Partition.Failed)
                {
                    Row(table, "failed", candidate, reason);
                }

                foreach (HeldBackPackage held in check.Partition.HeldBack)
                {
                    table.AddRow(
                        held.IsBlocked ? "blocked" : "held back",
                        Markup.Escape(held.Id),
                        Markup.Escape(held.Name),
                        Markup.Escape(held.Package.InstalledVersion ?? "unknown"),
                        Markup.Escape(held.Package.OfferedVersion ?? "unknown"),
                        Markup.Escape(held.Reason)
                    );
                }

                if (result.GetValue(everything))
                {
                    foreach (
                        PackageInfo package in check
                            .All.Where(static package => !package.IsUpdateAvailable)
                            .OrderBy(static package => package.Id, StringComparer.Ordinal)
                    )
                    {
                        table.AddRow(
                            "current",
                            Markup.Escape(package.Id),
                            Markup.Escape(package.Name),
                            Markup.Escape(package.InstalledVersion ?? "unknown"),
                            Markup.Escape(package.AvailableVersion ?? "unknown"),
                            ""
                        );
                    }
                }

                AnsiConsole.Write(table);
                AnsiConsole.MarkupLineInterpolated(
                    CultureInfo.InvariantCulture,
                    $"[grey]{check.Partition.Total} updatable of {check.All.Count} tracked by winget, {check.Tools.Count} tools[/]"
                );
                return 0;
            }
        );
        return command;
    }

    private static void Row(Table table, string section, UpdateCandidate candidate, string note) =>
        table.AddRow(
            section,
            Markup.Escape(candidate.Id),
            Markup.Escape(candidate.Name),
            Markup.Escape(candidate.Package.InstalledVersion ?? "unknown"),
            Markup.Escape(candidate.Package.AvailableVersion ?? "unknown"),
            Markup.Escape(note)
        );

    private static Command BuildUpdateAll()
    {
        Command command = new(
            "update-all",
            "Upgrade every eligible package in an elevated progress window."
        );
        command.SetAction(
            async (_, cancellationToken) =>
            {
                try
                {
                    int count = await ActionDispatcher.UpgradeAllAsync(cancellationToken);
                    if (count > 0)
                    {
                        AnsiConsole.MarkupLineInterpolated(
                            CultureInfo.InvariantCulture,
                            $"[green]✓[/] upgrading {"package".ToQuantity(count)}"
                        );
                    }
                    else
                    {
                        AnsiConsole.MarkupLine("[grey]·[/] nothing eligible");
                    }

                    return 0;
                }
                catch (Win32Exception exception)
                {
                    if (Launcher.IsElevationDeclined(exception))
                    {
                        AnsiConsole.MarkupLine("[red]✗[/] elevation declined");
                    }
                    else
                    {
                        AnsiConsole.MarkupLineInterpolated(
                            CultureInfo.InvariantCulture,
                            $"[red]✗[/] launch failed: {exception.Message}"
                        );
                    }

                    return 1;
                }
            }
        );
        return command;
    }

    private static Command BuildRegister()
    {
        Option<bool> noShortcut = new("--no-shortcut")
        {
            Description = "Skip the Start Menu shortcut; the installer creates its own.",
        };
        Command command = new(
            "register",
            "Register the notification, Start Menu shortcut and scheduled check."
        )
        {
            noShortcut,
        };
        command.SetAction(result =>
        {
            AppServices services = AppServices.Current;
            StartupRegistrar registrar = services.Registrar;
            Settings settings = Settings.Load(services.Paths);
            if (registrar.RegisterScheduledTask(settings))
            {
                AnsiConsole.MarkupLineInterpolated(
                    CultureInfo.InvariantCulture,
                    $"[green]✓[/] scheduled task  {registrar.CheckTaskName}"
                );
            }
            else
            {
                AnsiConsole.MarkupLineInterpolated(
                    CultureInfo.InvariantCulture,
                    $"[grey]·[/] no check triggers enabled; {registrar.CheckTaskName} removed"
                );
            }

            if (registrar.RegisterBackgroundTask(settings))
            {
                AnsiConsole.MarkupLineInterpolated(
                    CultureInfo.InvariantCulture,
                    $"[green]✓[/] quiet check     every {"hour".ToQuantity(settings.BackgroundCheckHours)}"
                );
            }
            else
            {
                AnsiConsole.MarkupLineInterpolated(
                    CultureInfo.InvariantCulture,
                    $"[grey]·[/] quiet check off; {registrar.BackgroundTaskName} removed"
                );
            }

            if (!result.GetValue(noShortcut))
            {
                registrar.CreateShortcut();
                AnsiConsole.MarkupLineInterpolated(
                    CultureInfo.InvariantCulture,
                    $"[green]✓[/] shortcut        {registrar.ShortcutPath}"
                );
            }

            AnsiConsole.MarkupLineInterpolated(
                CultureInfo.InvariantCulture,
                $"[green]✓[/] notifications   registered for {services.ExecutablePath}"
            );
        });
        return command;
    }

    private static Command BuildUnregister()
    {
        Option<bool> keepShortcut = new("--keep-shortcut")
        {
            Description = "Leave the Start Menu shortcut; the installer removes its own.",
        };
        Command command = new(
            "unregister",
            "Remove the scheduled check, shortcut and notification registration."
        )
        {
            keepShortcut,
        };
        command.SetAction(result =>
        {
            StartupRegistrar registrar = AppServices.Current.Registrar;
            registrar.UnregisterScheduledTasks();
            if (!result.GetValue(keepShortcut))
            {
                registrar.DeleteShortcut();
            }

            Microsoft.Windows.AppNotifications.AppNotificationManager.Default.UnregisterAll();
            AnsiConsole.MarkupLine("[green]✓[/] unregistered");
        });
        return command;
    }

    private static Command BuildTracking()
    {
        Command init = new(
            "init",
            "Seed version tracking with every installed package so the cooldown has a baseline."
        );
        init.SetAction(
            async (_, cancellationToken) =>
            {
                AppServices services = AppServices.Current;
                IReadOnlyList<PackageInfo> all = await services.Winget.GetInstalledAsync(
                    cancellationToken
                );
                if (all.Count == 0)
                {
                    AnsiConsole.MarkupLine("[yellow]![/] no winget-managed packages found");
                    return 1;
                }

                Dictionary<string, Dictionary<string, VersionObservation>> tracking =
                    services.Tracker.Reconcile(all);
                int versions = tracking.Values.Sum(static entry => entry.Count);
                AnsiConsole.MarkupLineInterpolated(
                    CultureInfo.InvariantCulture,
                    $"[green]✓[/] tracking seeded  {"version".ToQuantity(versions)} across {"package".ToQuantity(tracking.Count)}"
                );
                return 0;
            }
        );

        Command tracking = new("tracking", "Version tracking behind the cooldown gate.") { init };
        return tracking;
    }

    // ///// Manual tools /////

    private static Command BuildTool()
    {
        Command tool = new("tool", "Tools installed outside winget that the picker probes.")
        {
            BuildToolList(),
            BuildToolAdd(),
            BuildToolRemove(),
            BuildToolTest(),
        };
        return tool;
    }

    private static Command BuildToolList()
    {
        Command command = new("list", "List registered tools.");
        command.SetAction(_ =>
        {
            Dictionary<string, ToolDefinition> tools = AppServices.Current.Tools.Load();
            if (tools.Count == 0)
            {
                AnsiConsole.MarkupLine("[grey]·[/] no tools registered");
                return;
            }

            Table table = new Table()
                .Border(TableBorder.None)
                .AddColumns("id", "name", "current command", "latest url");
            foreach (
                (string id, ToolDefinition definition) in tools.OrderBy(
                    static pair => pair.Key,
                    StringComparer.Ordinal
                )
            )
            {
                table.AddRow(
                    Markup.Escape(id),
                    Markup.Escape(definition.Name),
                    Markup.Escape(string.Join(' ', definition.CurrentCommand)),
                    Markup.Escape(definition.LatestUrl)
                );
            }

            AnsiConsole.Write(table);
        });
        return command;
    }

    private static Command BuildToolAdd()
    {
        Argument<string> id = new("id") { Description = "Stable identifier, for example 'bun'." };
        Option<string> name = new("--name") { Description = "Display name.", Required = true };
        Option<string[]> current = new("--current")
        {
            Description = "Executable and arguments that print the installed version.",
            Required = true,
            AllowMultipleArgumentsPerToken = true,
        };
        Option<string?> currentRegex = new("--current-regex")
        {
            Description = "Regex with one group applied to the output.",
        };
        Option<string> latestUrl = new("--latest-url")
        {
            Description = "JSON endpoint naming the latest version.",
            Required = true,
        };
        Option<string> latestField = new("--latest-field")
        {
            Description = "Top-level JSON field holding the version.",
            Required = true,
        };
        Option<string?> latestRegex = new("--latest-regex")
        {
            Description = "Regex with one group applied to the field.",
        };
        Option<string> upgrade = new("--upgrade")
        {
            Description = "Shell command that upgrades the tool.",
            Required = true,
        };

        Command command = new("add", "Add or replace a tool.")
        {
            id,
            name,
            current,
            currentRegex,
            latestUrl,
            latestField,
            latestRegex,
            upgrade,
        };
        command.SetAction(result =>
        {
            string toolId = result.GetValue(id) ?? "";
            ToolDefinition definition = new()
            {
                Name = result.GetValue(name) ?? "",
                CurrentCommand = result.GetValue(current) ?? [],
                CurrentRegex = result.GetValue(currentRegex),
                LatestUrl = result.GetValue(latestUrl) ?? "",
                LatestJsonField = result.GetValue(latestField) ?? "",
                LatestRegex = result.GetValue(latestRegex),
                UpgradeCommand = result.GetValue(upgrade) ?? "",
            };
            AppServices.Current.Tools.Register(toolId, definition);
            AnsiConsole.MarkupLineInterpolated(
                CultureInfo.InvariantCulture,
                $"[green]✓[/] registered {toolId}"
            );
        });
        return command;
    }

    private static Command BuildToolRemove()
    {
        Argument<string> id = new("id") { Description = "Tool identifier." };
        Command command = new("remove", "Remove a tool and its cached probe.") { id };
        command.SetAction(result =>
        {
            string toolId = result.GetValue(id) ?? "";
            bool removed = AppServices.Current.Tools.Unregister(toolId);
            if (removed)
            {
                AnsiConsole.MarkupLineInterpolated(
                    CultureInfo.InvariantCulture,
                    $"[green]✓[/] removed {toolId}"
                );
            }
            else
            {
                AnsiConsole.MarkupLineInterpolated(
                    CultureInfo.InvariantCulture,
                    $"[grey]·[/] {toolId} was not registered"
                );
            }

            return removed ? 0 : 1;
        });
        return command;
    }

    private static Command BuildToolTest()
    {
        Argument<string> id = new("id") { Description = "Tool identifier." };
        Option<bool> force = new("--force") { Description = "Bypass the latest-version cache." };
        Command command = new("test", "Run both probes for a tool and report the result.")
        {
            id,
            force,
        };
        command.SetAction(
            async (result, cancellationToken) =>
            {
                string toolId = result.GetValue(id) ?? "";
                AppServices services = AppServices.Current;
                Dictionary<string, ToolDefinition> tools = services.Tools.Load();
                if (!tools.TryGetValue(toolId, out ToolDefinition? definition))
                {
                    AnsiConsole.MarkupLineInterpolated(
                        CultureInfo.InvariantCulture,
                        $"[red]✗[/] {toolId} is not registered"
                    );
                    return 1;
                }

                string? currentVersion = await services.ToolProber.GetCurrentAsync(
                    definition,
                    cancellationToken
                );
                string? latestVersion = await services.ToolProber.GetLatestAsync(
                    toolId,
                    definition,
                    result.GetValue(force),
                    cancellationToken
                );
                ToolStatus status = new(toolId, definition, currentVersion, latestVersion);
                AnsiConsole.Markup(status.UpdateAvailable ? "[yellow]![/] " : "[green]✓[/] ");
                AnsiConsole.MarkupLineInterpolated(
                    CultureInfo.InvariantCulture,
                    $"{status.Name}  current {currentVersion ?? "?"}  latest {latestVersion ?? "?"}"
                );
                return 0;
            }
        );
        return command;
    }
}
