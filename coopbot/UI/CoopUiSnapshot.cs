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
    IReadOnlyList<CoopUiRouteStep> Route,
    IReadOnlyList<CoopUiActorSummary> Actors,
    bool ShowHostControls,
    bool ShowClientControls,
    string ReplanLabel,
    string CancelSearchLabel,
    string ExecuteNextLabel,
    string PauseLabel,
    string AllowOnceLabel,
    string RejectLabel)
{
    internal static CoopUiSnapshot Capture(
        PlanPublishedPayload plan,
        CoopUiRole role,
        int localActorId,
        CoopAutomationMode mode,
        string statusKey,
        int currentActionIndex,
        string locale)
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
        CoopUiActorSummary[] actors = plan.Actors.Select(actor => new CoopUiActorSummary(
            actor.ActorId,
            CoopBotText.Format(
                "角色 {0}: {1}/{2} HP，战损 {3}",
                locale,
                actor.ActorId + 1,
                actor.Hp,
                actor.MaxHp,
                actor.HpLost))).ToArray();
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
            CoopBotText.Format("计划 {0} · 根 {1}", locale, plan.PlanId[..8], plan.RootRevision),
            CoopBotText.Format("角色 {0} · {1} · {2}", locale, localActorId + 1, roleText, $"{modeText} / {status}"),
            CoopBotText.Format(
                "总战损 {0} · 药水 {1} · {2}",
                locale,
                plan.Score.TotalHpLost,
                plan.Score.PotionUses,
                plan.Termination),
            current,
            Array.AsReadOnly(route),
            Array.AsReadOnly(actors),
            role == CoopUiRole.Host,
            role == CoopUiRole.Client,
            CoopBotText.Get("重新搜索", locale),
            CoopBotText.Get("取消搜索", locale),
            CoopBotText.Get("执行下一步", locale),
            CoopBotText.Get("暂停自动", locale),
            CoopBotText.Get("允许本次", locale),
            CoopBotText.Get("拒绝并转人工", locale));
    }

    private static string Display(string title, string id)
        => string.IsNullOrWhiteSpace(title) ? id : title;
}
