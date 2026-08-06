using DesktopPet.App.Ai;
using DesktopPet.Core.Scheduling;
using DesktopPet.Infra.Providers;

namespace DesktopPet.App.Tests;

public sealed class ModelConnectionTestControllerTests
{
    private sealed class FakeTester : IModelConnectionTester
    {
        public Queue<TaskCompletionSource<ModelConnectionTestResult>> Pending { get; } = new();
        public List<ModelConnectionDraft> Drafts { get; } = [];

        public Task<ModelConnectionTestResult> TestAsync(ModelConnectionDraft draft, CancellationToken ct)
        {
            Drafts.Add(draft);
            var pending = new TaskCompletionSource<ModelConnectionTestResult>(
                TaskCreationOptions.RunContinuationsAsynchronously);
            Pending.Enqueue(pending);
            return pending.Task; // deliberately ignores cancellation to verify generation filtering
        }
    }

    [Fact]
    public async Task NewTestMakesLateOldResultStale()
    {
        var tester = new FakeTester();
        using var controller = new ModelConnectionTestController(tester);
        var first = controller.TestLatestAsync(Draft("first"));
        var firstPending = tester.Pending.Dequeue();
        var second = controller.TestLatestAsync(Draft("second"));
        var secondPending = tester.Pending.Dequeue();

        secondPending.SetResult(Success("second"));
        var secondResult = await second;
        firstPending.SetResult(Success("first"));
        var firstResult = await first;

        Assert.Equal("second", secondResult!.Message);
        Assert.Null(firstResult);
        Assert.Equal(["first", "second"], tester.Drafts.Select(draft => draft.ModelName));
    }

    [Fact]
    public async Task CancelInvalidatesLateResult()
    {
        var tester = new FakeTester();
        using var controller = new ModelConnectionTestController(tester);
        var task = controller.TestLatestAsync(Draft("stale"));
        var pending = tester.Pending.Dequeue();

        controller.Cancel();
        pending.SetResult(Success("stale"));

        Assert.Null(await task);
    }

    private static ModelConnectionDraft Draft(string model)
        => new("https://example.com/v1", model, "", "draft-secret", ModelCapabilities.Chat);

    private static ModelConnectionTestResult Success(string message)
        => new(true, "ok", message, []);
}
