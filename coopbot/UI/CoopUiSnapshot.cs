using CoopBot.Protocol;

namespace CoopBot.UI;

internal enum CoopUiRole
{
    Host,
    Client,
}

internal sealed record CoopUiRouteStep(
    int Index,
    int ActorId,
    string Text,
    bool IsCurrent);

internal sealed record CoopUiActorSummary(
    int ActorId,
    string Text);

internal sealed record CoopUiSnapshot(
    string PlanId,
    long RootRevision,
    CoopUiRole Role,
    int LocalActorId,
    string Title,
    string SessionLine,
    string StatusLine,
    string ScoreLine,
    string CurrentActionLine,
    string DetailLine,
    IReadOnlyList<CoopUiRouteStep> Route,
    IReadOnlyList<CoopUiActorSummary> Actors,
    bool ShowHostControls,
    bool ShowClientControls,
    string ReplanLabel,
    string CancelSearchLabel,
    string ExecuteNextLabel,
    string PauseLabel,
    string AllowOnceLabel,
    string RejectLabel,
    string ObserveLabel,
    string SuggestLabel,
    string ConfirmEachLabel,
    string AutoLabel)
{
    internal static CoopUiSnapshot Capture(
        PlanPublishedPayload plan,
        CoopUiRole role,
        int localActorId,
        CoopAutomationMode mode,
        string statusKey,
        int currentActionIndex,
        string locale)
        => Capture(
            plan,
            role,
            localActorId,
            mode,
            statusKey,
            currentActionIndex,
            locale,
            bindings: null,
            hostActorId: role == CoopUiRole.Host ? localActorId : 0,
            connectionKey: "已连接",
            detail: "-");

    internal static CoopUiSnapshot Capture(
        PlanPublishedPayload plan,
        CoopUiRole role,
        int localActorId,
        CoopAutomationMode mode,
        string statusKey,
        int currentActionIndex,
        string locale,
        IReadOnlyCollection<ActorBinding>? bindings,
        int hostActorId,
        string connectionKey,
        string detail)
    {
        CoopUiRouteStep[] route = plan.Actions.Select(action =>
        {
            string actionText = action.Kind switch
            {
                "PlayCard" => CoopBotText.Format("打出 {0}", locale, Display(action.CardTitle, action.CardId)),
                "UsePotion" => CoopBotText.Format("使用药水 {0}", locale, Display(action.PotionTitle, action.PotionId)),
                "EndTurn" => CoopBotText.Get("结束回合", locale),
                _ => action.Kind,
            };
            if (!string.IsNullOrWhiteSpace(action.TargetName))
                actionText += CoopBotText.Format(" → {0}", locale, action.TargetName);
            return new CoopUiRouteStep(
                action.Index,
                action.ActorId,
                CoopBotText.Format(
                    "{0}. 角色 {1}：{2}",
                    locale,
                    action.Index + 1,
                    action.ActorId + 1,
                    actionText),
                action.Index == currentActionIndex);
        }).ToArray();
        IReadOnlyDictionary<int, ActorBinding> bindingByActor = bindings?
            .ToDictionary(binding => binding.ActorId) ?? new Dictionary<int, ActorBinding>();
        CoopUiActorSummary[] actors = plan.Actors.Select(actor =>
        {
            string identity = bindingByActor.TryGetValue(actor.ActorId, out ActorBinding? binding)
                ? CoopBotText.Format(
                    "{0} · {1}",
                    locale,
                    binding.CharacterId,
                    CoopBotText.Get(binding.IsHost ? "Host" : "Client", locale))
                : CoopBotText.Get(actor.ActorId == hostActorId ? "Host" : "Client", locale);
            return new CoopUiActorSummary(
                actor.ActorId,
                CoopBotText.Format(
                    "角色 {0}: {1} · {2} · {3}/{4} HP，战损 {5}",
                    locale,
                    actor.ActorId + 1,
                    identity,
                    CoopBotText.Get(connectionKey, locale),
                    actor.Hp,
                    actor.MaxHp,
                    actor.HpLost));
        }).ToArray();
        string roleText = CoopBotText.Get(role == CoopUiRole.Host ? "Host" : "Client", locale);
        string modeText = CoopBotText.Get(mode switch
        {
            CoopAutomationMode.Observe => "观察",
            CoopAutomationMode.Suggest => "建议",
            CoopAutomationMode.ConfirmEach => "逐步确认",
            CoopAutomationMode.Auto => "自动",
            _ => throw new ArgumentOutOfRangeException(nameof(mode)),
        }, locale);
        string status = CoopBotText.Get(statusKey, locale);
        string current = currentActionIndex >= 0 && currentActionIndex < route.Length
            ? route[currentActionIndex].Text
            : CoopBotText.Get("当前动作：无", locale);
        return new CoopUiSnapshot(
            plan.PlanId,
            plan.RootRevision,
            role,
            localActorId,
            CoopBotText.Get("协作机器人", locale),
            CoopBotText.Format(
                "协议 v{0} · {1} · Host 角色 {2} · 计划 {3} · 根 {4}",
                locale,
                CoopProtocol.Version,
                CoopBotText.Get(connectionKey, locale),
                hostActorId + 1,
                plan.PlanId[..8],
                plan.RootRevision),
            CoopBotText.Format("角色 {0} · {1} · {2}", locale, localActorId + 1, roleText, $"{modeText} / {status}"),
            CoopBotText.Format(
                "总战损 {0} · 药水 {1} · {2}",
                locale,
                plan.Score.TotalHpLost,
                plan.Score.PotionUses,
                plan.Termination),
            current,
            CoopBotText.Format("最近结果：{0}", locale, detail),
            Array.AsReadOnly(route),
            Array.AsReadOnly(actors),
            role == CoopUiRole.Host,
            role == CoopUiRole.Client,
            CoopBotText.Get("重新搜索", locale),
            CoopBotText.Get("取消搜索", locale),
            CoopBotText.Get("执行下一步", locale),
            CoopBotText.Get("暂停自动", locale),
            CoopBotText.Get("允许本次", locale),
            CoopBotText.Get("拒绝并转人工", locale),
            CoopBotText.Get("观察", locale),
            CoopBotText.Get("建议", locale),
            CoopBotText.Get("逐步确认", locale),
            CoopBotText.Get("自动", locale));
    }

    private static string Display(string title, string id)
        => string.IsNullOrWhiteSpace(title) ? id : title;
}
