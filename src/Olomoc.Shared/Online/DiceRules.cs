using Olomoc.Tournaments;

namespace Olomoc.Online;

/// <summary>
/// Shared dice roller of an online game: two slots, the action and the reaction under it (an attack
/// and the dodge or riposte answering it, rulebook p.21-22), open to both players. The server rolls
/// the D6 (fair, the same for both); the faces are read at a threshold (2+ to 5+: 5+ by default,
/// lowered by support or a charge, rulebook p.10) that either player changes for both. "Reroll the
/// failures": the dice under the threshold again (trying fate, rulebook p.10; the Darkness token stays
/// on the player's sheet); a whole new roll is "Roll" again.
/// From the initiative to the end of the game, like the table.
/// </summary>
public static class DiceRules
{
    public const string Action = "action", Reaction = "reaction";
    public static readonly IReadOnlyList<string> Slots = [Action, Reaction];
    public const int MaxDice = 12, MinThreshold = 2, MaxThreshold = 5, DefaultThreshold = 5;

    public static int Successes(IEnumerable<int> faces, int threshold) => faces.Count(f => f >= threshold);

    public static Problem? Apply(OnlineGame g, int seat, OnlineCommand c, Random random, DateTimeOffset now)
    {
        if (g.Step is OnlineStep.Lobby or OnlineStep.Finished || g.Seats.Any(s => s.State is null)) return Problem.Of("onlineStep");
        if (c.Text is not { } slot || !Slots.Contains(slot)) return Problem.Of("onlineDice");
        if (c.Threshold is { } t && (t < MinThreshold || t > MaxThreshold)) return Problem.Of("onlineDiceThreshold", MinThreshold.ToString(), MaxThreshold.ToString());
        var dice = g.Dice ??= new();
        var roll = dice.GetValueOrDefault(slot);
        switch (c.Type)
        {
            case OnlineCommandType.RollDice:
                if (c.Count is not { } count || count < 1 || count > MaxDice) return Problem.Of("onlineDiceCount", MaxDice.ToString());
                roll = dice[slot] = new OnlineDiceRoll
                {
                    Faces = Roll(random, count), Threshold = c.Threshold ?? roll?.Threshold ?? DefaultThreshold, Seat = seat, At = now,
                };
                Log(g, seat, now, "dice", slot, roll);
                return null;
            case OnlineCommandType.RerollDice:
                if (roll is null) return Problem.Of("onlineDice");
                // The failures only (under the threshold): the successes stay.
                if (roll.Faces.All(face => face >= roll.Threshold)) return null;
                roll.Faces = roll.Faces.Select(face => face >= roll.Threshold ? face : random.Next(1, 7)).ToList();
                roll.Rerolls++;
                roll.Seat = seat;
                roll.At = now;
                Log(g, seat, now, "diceReroll", slot, roll);
                return null;
            case OnlineCommandType.SetDiceThreshold:
                if (roll is null || c.Threshold is not { } threshold) return Problem.Of("onlineDice");
                roll.Threshold = threshold;
                return null;
            default:
                return Problem.Of("onlineCommand");
        }
    }

    private static List<int> Roll(Random random, int count) => Enumerable.Range(0, count).Select(_ => random.Next(1, 7)).ToList();

    // "Ana: action, 4 D6: 6 5 2 1 (2 successes at 5+)".
    private static void Log(OnlineGame g, int seat, DateTimeOffset now, string key, string slot, OnlineDiceRoll roll) =>
        OnlineRules.Log(g, seat, now, key, slot, string.Join(" ", roll.Faces), Successes(roll.Faces, roll.Threshold).ToString(),
            roll.Threshold.ToString(), roll.Faces.Count.ToString());
}
