using System.Threading;
using System.Threading.Tasks;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using OhMyAgent.AiAgent.Client.Models;

namespace OhMyAgent.AiAgent.Client.ViewModels;

/// <summary>
/// Backs the inline Manual-mode approval card. The orchestrator (via the
/// PermissionService approval handler) awaits <see cref="WaitForDecisionAsync"/>;
/// the View's Allow/Deny/AlwaysAllow buttons complete the underlying
/// <see cref="TaskCompletionSource{TResult}"/>.
/// </summary>
public sealed partial class ApprovalRequestViewModel : ObservableObject
{
    private readonly TaskCompletionSource<PermissionDecision> _tcs =
        new(TaskCreationOptions.RunContinuationsAsynchronously);

    public string ToolName { get; }
    public ToolRisk Risk { get; }

    /// <summary>Pretty-printed JSON arguments for the pending tool call.</summary>
    public string ArgsPreview { get; }

    public ApprovalRequestViewModel(string toolName, ToolRisk risk, string argsPreview)
    {
        ToolName = toolName;
        Risk = risk;
        ArgsPreview = argsPreview;
    }

    [RelayCommand]
    private void Allow() => _tcs.TrySetResult(PermissionDecision.Allow);

    [RelayCommand]
    private void Deny() => _tcs.TrySetResult(PermissionDecision.Deny);

    [RelayCommand]
    private void AlwaysAllow() => _tcs.TrySetResult(PermissionDecision.AlwaysAllow);

    /// <summary>
    /// Awaited by the approval handler. Resolves when the user picks an option,
    /// or when <paramref name="ct"/> is cancelled (treated as <see cref="PermissionDecision.Deny"/>).
    /// </summary>
    public async Task<PermissionDecision> WaitForDecisionAsync(CancellationToken ct)
    {
        await using (ct.Register(static state =>
                         ((TaskCompletionSource<PermissionDecision>)state!).TrySetResult(PermissionDecision.Deny),
                     _tcs).ConfigureAwait(false))
        {
            return await _tcs.Task.ConfigureAwait(false);
        }
    }
}
