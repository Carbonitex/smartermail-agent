using System.Security.Cryptography;
using Microsoft.Extensions.Logging.Abstractions;
using SmarterMailAgent.Server;
using SmarterMailAgent.Storage;
using SmarterMailAgent.Profiles;
using SmarterMailAgent.Tasks;

namespace SmarterMailAgent.Tests;

/// <summary>The scheduled-run prompt tells the model when the previous run happened.</summary>
public sealed class TaskPromptTests
{
    private static readonly DateTimeOffset Now = new(2026, 10, 9, 14, 0, 0, TimeSpan.Zero);
    private static readonly TimeZoneInfo Phoenix = TimeZoneInfo.FindSystemTimeZoneById("America/Phoenix");

    private static string Build(TaskPrompt.History? history) =>
        TaskPrompt.Build("Morning", [], [], 0, false, Now, Phoenix, history);

    [Fact]
    public void First_run_says_so()
    {
        foreach (var prompt in new[] { Build(null), Build(TaskPrompt.History.None) })
        {
            Assert.Contains("first run of this task", prompt);
            Assert.DoesNotContain("Previous successful run", prompt);
        }
    }

    [Fact]
    public void Previous_success_is_shown_in_the_task_zone()
    {
        // 14:00 UTC on the 8th = 07:00 in Phoenix (UTC-7, no DST).
        var prompt = Build(new TaskPrompt.History(new DateTimeOffset(2026, 10, 8, 14, 0, 0, TimeSpan.Zero), null));
        Assert.Contains("Previous successful run: 2026-10-08 07:00 (America/Phoenix). Report only items since then unless the task says otherwise.", prompt);
        Assert.DoesNotContain("first run", prompt);
        Assert.DoesNotContain("did not complete", prompt);
    }

    [Fact]
    public void A_later_failure_is_mentioned_next_to_the_last_success()
    {
        var prompt = Build(new TaskPrompt.History(
            new DateTimeOffset(2026, 10, 7, 14, 0, 0, TimeSpan.Zero), new DateTimeOffset(2026, 10, 8, 14, 0, 0, TimeSpan.Zero)));
        Assert.Contains("Previous successful run: 2026-10-07 07:00", prompt);
        Assert.Contains("most recent attempt (2026-10-08 07:00 (America/Phoenix)) did not complete", prompt);
    }

    [Fact]
    public void Failure_without_any_success_is_still_a_first_success()
    {
        var prompt = Build(new TaskPrompt.History(null, new DateTimeOffset(2026, 10, 8, 14, 0, 0, TimeSpan.Zero)));
        Assert.Contains("first run", prompt);
        Assert.Contains("did not complete", prompt);
    }

    [Fact]
    public void Store_ignores_test_runs_the_current_run_and_running_runs()
    {
        var db = new DataStore(new ServerOptions { DataDir = TestEnvironment.NewDataDir() }, NullLogger<DataStore>.Instance);
        var profiles = new ProfileStore(db);
        var tasks = new TaskStore(db);
        var profileId = ProfileCrypto.NewId();
        var now = DataStore.Now();
        profiles.CreateProfile(
            new ProfileRow(profileId, now, now, "pub", "priv", null, 0, ProfileCrypto.AccountsKeyCheck(RandomNumberGenerator.GetBytes(32)), null, null, null, false),
            new PasskeyRow(ProfileCrypto.NewId(), profileId, [1], 0, null, null, "w", now, null), []);
        tasks.Insert(new TaskRow("t1", profileId, true, "def", null, "ok", 0, null, now, now));

        Assert.Equal((null, null), tasks.PreviousRuns("t1", "none"));

        void Run(string id, long at, bool dry, string status)
        {
            tasks.StartRun(new TaskRunRow(id, "t1", profileId, at, null, "running", dry, "schedule", null, 0, 0, null, null, null, false));
            if (status != "running")
                tasks.FinishRun(id, status, status == "ok" ? null : "ERROR", 0, 0, null, null, null);
        }

        Run("ok1", 1000, false, "ok");
        Run("test-ok", 2000, true, "ok");        // newer, but a test run
        var (lastOk, latest) = tasks.PreviousRuns("t1", "current");
        Assert.Equal("ok1", lastOk!.Id);
        Assert.Equal("ok1", latest!.Id);

        Run("fail1", 3000, false, "failed");
        Run("live", 4000, false, "running");     // still running: not a previous run
        (lastOk, latest) = tasks.PreviousRuns("t1", "current");
        Assert.Equal("ok1", lastOk!.Id);
        Assert.Equal("fail1", latest!.Id);

        (_, latest) = tasks.PreviousRuns("t1", "fail1");   // the run being started is excluded
        Assert.Equal("ok1", latest!.Id);
    }
}
