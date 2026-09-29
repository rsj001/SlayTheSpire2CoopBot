namespace CombatSolver;

internal enum JointActorTurnPhase
{
    Playing,
    Ended,
    Dead,
}

/// <summary>
/// Immutable barrier state for an offline joint turn. Actor order is stable, but action order
/// within a turn is not forced: any living Actor may act until it reaches its own EndTurn.
/// </summary>
internal sealed record JointTurnState
{
    private JointTurnState(int turn, JointActorTurnPhase[] phases)
    {
        if (turn <= 0)
            throw new ArgumentOutOfRangeException(nameof(turn), turn, "联合回合编号必须为正数。");
        if (phases.Length == 0)
            throw new ArgumentException("联合回合必须至少包含一个 Actor。", nameof(phases));
        Turn = turn;
        Phases = phases;
    }

    public int Turn { get; }
    public IReadOnlyList<JointActorTurnPhase> Phases { get; }
    public int ActorCount => Phases.Count;

    public static JointTurnState Start(int actorCount, int turn = 1)
    {
        if (actorCount <= 0)
            throw new ArgumentOutOfRangeException(nameof(actorCount), actorCount, "联合回合必须至少包含一个 Actor。");
        return new JointTurnState(turn, Enumerable.Repeat(JointActorTurnPhase.Playing, actorCount).ToArray());
    }

    public bool IsActionable(CombatActorId actor)
        => TryGetPhase(actor) == JointActorTurnPhase.Playing;

    public bool IsBarrierReached
        => Phases.All(static phase => phase is JointActorTurnPhase.Ended or JointActorTurnPhase.Dead);

    public JointTurnState EndTurn(CombatActorId actor)
    {
        int index = ValidateActor(actor);
        JointActorTurnPhase phase = Phases[index];
        if (phase != JointActorTurnPhase.Playing)
            throw new InvalidOperationException($"{actor} 当前阶段为 {phase}，不能重复结束回合。");
        JointActorTurnPhase[] next = [.. Phases];
        next[index] = JointActorTurnPhase.Ended;
        return new JointTurnState(Turn, next);
    }

    public JointTurnState UndoEndTurn(CombatActorId actor)
    {
        if (IsBarrierReached)
        {
            throw new InvalidOperationException(
                "联合回合已到达全员屏障，不能撤销可能已触发结算的 EndTurn。");
        }
        int index = ValidateActor(actor);
        JointActorTurnPhase phase = Phases[index];
        if (phase != JointActorTurnPhase.Ended)
            throw new InvalidOperationException($"{actor} 当前阶段为 {phase}，没有可撤销的 EndTurn。");
        JointActorTurnPhase[] next = [.. Phases];
        next[index] = JointActorTurnPhase.Playing;
        return new JointTurnState(Turn, next);
    }

    public JointTurnState MarkDead(CombatActorId actor)
    {
        int index = ValidateActor(actor);
        if (Phases[index] == JointActorTurnPhase.Dead)
            return this;
        JointActorTurnPhase[] next = [.. Phases];
        next[index] = JointActorTurnPhase.Dead;
        return new JointTurnState(Turn, next);
    }

    public JointTurnState AdvanceTurn()
    {
        if (!IsBarrierReached)
            throw new InvalidOperationException("仍有存活 Actor 未到达结束回合屏障。");
        JointActorTurnPhase[] next = [.. Phases];
        for (int index = 0; index < next.Length; index++)
        {
            if (next[index] != JointActorTurnPhase.Dead)
                next[index] = JointActorTurnPhase.Playing;
        }
        return new JointTurnState(Turn + 1, next);
    }

    public JointTurnState AdvanceExtraTurn(IReadOnlySet<CombatActorId> activeActors)
    {
        if (!IsBarrierReached)
            throw new InvalidOperationException("仍有存活 Actor 未到达结束回合屏障。");
        if (activeActors.Count == 0)
            throw new ArgumentException("额外回合至少需要一个 Actor。", nameof(activeActors));
        JointActorTurnPhase[] next = [.. Phases];
        for (int index = 0; index < next.Length; index++)
        {
            if (next[index] == JointActorTurnPhase.Dead)
                continue;
            next[index] = activeActors.Contains(new CombatActorId(index))
                ? JointActorTurnPhase.Playing
                : JointActorTurnPhase.Ended;
        }
        if (!next.Any(static phase => phase == JointActorTurnPhase.Playing))
            throw new InvalidOperationException("额外回合 Actor 集合没有存活成员。");
        return new JointTurnState(Turn + 1, next);
    }

    private JointActorTurnPhase? TryGetPhase(CombatActorId actor)
        => (uint)actor.Index < (uint)Phases.Count ? Phases[actor.Index] : null;

    private int ValidateActor(CombatActorId actor)
    {
        if ((uint)actor.Index >= (uint)Phases.Count)
            throw new InvalidOperationException($"Actor {actor} 不在 ActorCount={ActorCount} 范围内。");
        return actor.Index;
    }
}
