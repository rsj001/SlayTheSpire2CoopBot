namespace CombatSolver;

internal sealed record JointChoiceContinuation(IReadOnlyList<JointPendingChoiceFrame> Frames)
{
    internal static JointChoiceContinuation Empty { get; } = new([]);

    internal JointChoiceContinuation Enqueue(JointPendingChoiceFrame frame)
    {
        if (Frames.Any(candidate => candidate.OwnerActor == frame.OwnerActor))
        {
            throw new InvalidOperationException(
                $"Actor {frame.OwnerActor} 已有待处理联合选择，不能并存第二帧。");
        }
        return new JointChoiceContinuation(
            Array.AsReadOnly(Frames.Append(frame).ToArray()));
    }

    internal JointChoiceContinuation Consume(
        CombatActorId owner,
        PlanAction sourceAction,
        out JointPendingChoiceFrame frame)
    {
        if (Frames.Count == 0)
            throw new InvalidOperationException("联合选择 continuation 为空。");
        frame = Frames[0];
        if (frame.OwnerActor != owner || frame.SourceAction != sourceAction)
        {
            throw new InvalidOperationException(
                $"联合选择必须按原序由 {frame.OwnerActor} 消费，" +
                $"不能由 {owner} 或其他 SourceAction 抢占。");
        }
        return new JointChoiceContinuation(
            Array.AsReadOnly(Frames.Skip(1).ToArray()));
    }
}
