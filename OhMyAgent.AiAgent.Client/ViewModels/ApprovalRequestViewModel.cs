using System.Collections.Generic;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using OhMyAgent.AiAgent.Client.Models;
using OhMyAgent.AiAgent.Client.Services.Diff;
using OhMyAgent.AiAgent.Client.Services.Tools;

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

    /// <summary>
    /// edit_file 승인일 때 old_string→new_string 의 라인 diff(그 외엔 null). 있으면 카드가 생 JSON 대신
    /// diff 를 보여준다 — 사용자가 "무엇이 바뀌는지"를 보고 승인하도록.
    /// </summary>
    public IReadOnlyList<DiffLine>? Diff { get; }

    /// <summary>diff 헤더(대상 파일 경로). diff 가 있을 때만 채워진다.</summary>
    public string DiffPath { get; } = string.Empty;

    /// <summary>diff 표시 여부(XAML 바인딩 편의).</summary>
    public bool HasDiff => Diff is { Count: > 0 };

    public ApprovalRequestViewModel(string toolName, ToolRisk risk, string argsPreview, JsonElement args)
    {
        ToolName = toolName;
        Risk = risk;
        ArgsPreview = argsPreview;

        // edit_file: 인자에서 old/new 를 꺼내 diff 를 만든다. 도구명·스키마는 EditFileTool 과 일치.
        if (string.Equals(toolName, "edit_file", System.StringComparison.OrdinalIgnoreCase)
            && args.ValueKind == JsonValueKind.Object)
        {
            var oldStr = ToolSchemas.GetString(args, "old_string");
            var newStr = ToolSchemas.GetString(args, "new_string");
            if (!string.IsNullOrEmpty(oldStr) || !string.IsNullOrEmpty(newStr))
            {
                Diff = LineDiff.Compute(oldStr, newStr);
                DiffPath = ToolSchemas.GetString(args, "path") ?? string.Empty;
            }
        }
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
