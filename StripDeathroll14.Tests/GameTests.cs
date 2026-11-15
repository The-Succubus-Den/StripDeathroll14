using Xunit;

namespace StripDeathroll14.Tests;

public class GameTests
{
    private static DeathrollGame Game(params string[] names)
    {
        var g = new DeathrollGame();
        foreach (var n in names) Assert.True(g.Add(n));
        return g;
    }

    private static Player P(DeathrollGame g, string name) => g.Find(name)!;

    [Fact]
    public void ParsesRandomAndDice()
    {
        Assert.Equal(new ParsedRoll(null, 7, 10), Rolls.ParseRandom("Random! You roll a 7 (out of 10)."));
        Assert.Equal(new ParsedRoll("Ann Lee", 532, null), Rolls.ParseRandom("Random! Ann Lee rolls a 532."));
        Assert.Equal(new ParsedRoll("Ann Lee", 5, 532), Rolls.ParseRandom("Random! Ann Lee rolls a 5 (out of 532)."));
        Assert.Equal(new ParsedRoll(null, 7, 10), Rolls.ParseDice("Random! (1-10) 7"));
        Assert.Equal(new ParsedRoll(null, 812, null), Rolls.ParseDice("Random! 812"));
        Assert.Null(Rolls.ParseRandom("Ann Lee: Random! You roll a 7 (out of 10)."));
        Assert.Null(Rolls.ParseDice("Random! (1-10) 7 extra"));
    }

    // /dice lines come from Party chat, which anyone in the party can type: the parser must not throw and must
    // drop numbers no real roll produces.
    [Theory]
    [InlineData("Random! (1-99999999999) 5")]   // int overflow
    [InlineData("Random! 99999999999")]
    [InlineData("Random! (1-10) \u0663")]       // Arabic-Indic digit: \d matches it, int.Parse throws
    [InlineData("Random! (1-10) 0")]
    [InlineData("Random! (1-10) 11")]           // above the range
    [InlineData("Random! (1-0) 0")]
    public void DiceParserRejectsImpossibleOrForgedNumbers(string text) => Assert.Null(Rolls.ParseDice(text));

    [Theory]
    [InlineData("Random! Ann Lee rolls a 99999999999.")]
    [InlineData("Random! Ann Lee rolls a 11 (out of 10).")]
    [InlineData("Random! You roll a 0 (out of 10).")]
    public void RandomParserRejectsImpossibleNumbers(string text) => Assert.Null(Rolls.ParseRandom(text));

    [Fact]
    public void RollOfTwoKeepsTheLimitAtTwo()
    {
        var g = Opened(out _);
        Assert.Equal(RollOutcome.Next, g.OnRoll(P(g, "Ann Lee"), 2, null));
        Assert.Equal(2, g.Limit); // never "/random 1": the next roll can still be 1 or 2
        Assert.Equal(RollOutcome.WrongMax, g.OnRoll(P(g, "Bo Ray"), 1, 1));
        Assert.Equal(RollOutcome.Won, g.OnRoll(P(g, "Bo Ray"), 1, 2));
        Assert.Equal("Bo Ray", g.Winner!.Name);
    }

    [Fact]
    public void LastRivalLeavingDuringPickDoesNotStrandTheGame()
    {
        var g = Opened(out _);
        g.OnRoll(P(g, "Ann Lee"), 1, null);
        Assert.Equal(Phase.Pick, g.Phase);
        g.Remove(P(g, "Bo Ray"));
        Assert.NotEqual(Phase.Pick, g.Phase); // nobody left to pick; Pick would wait forever
        Assert.Null(g.Current);
        Assert.True(g.Add("Cy Dee"));
        Assert.True(g.CanStartRound);
    }

    [Fact]
    public void DroppingToSetupClearsTheTurn()
    {
        var g = Opened(out _);
        g.OnRoll(P(g, "Ann Lee"), 300, 10); // pending wrong max
        g.Remove(P(g, "Bo Ray"));
        Assert.Equal(Phase.Setup, g.Phase);
        Assert.Null(g.Current); // "Call again" must not call a turn that no longer exists
        Assert.Equal(RollOutcome.Ignored, g.AcceptPending());
    }

    [Fact]
    public void PickRejectsOutOrUnlistedTargets()
    {
        var g = Game("Ann Lee", "Bo Ray", "Cy Dee");
        for (var i = 0; i < 3; i++) WinRound(g, "Ann Lee", "Bo Ray");
        WinRoundNoPick(g, "Ann Lee");
        Assert.False(g.Pick(P(g, "Bo Ray")));                 // out
        Assert.False(g.Pick(new Player { Name = "Zed Zee" })); // not on the list
        Assert.DoesNotContain(g.History, r => r.Round == g.Round);
    }

    [Fact]
    public void CrossWorldRollerIsFoundAndSpectatorsAreNot()
    {
        var g = Game("Ann Lee", "Bo Ray");
        Assert.Equal("Ann Lee", g.Find("Ann Lee\uE05DCactuar")!.Name);
        Assert.Null(g.Find("Ann Leez"));
        Assert.Null(g.Find(""));
    }

    [Fact]
    public void SendableCapCountsBytesNotChars()
    {
        Assert.True(Lines.IsSendable(new string('あ', 133)));  // 399 bytes
        Assert.False(Lines.IsSendable(new string('あ', 134))); // 402 bytes
        Assert.False(Lines.IsSendable(""));
    }

    [Fact]
    public void NameMatchingHandlesGlyphsAndWorlds()
    {
        Assert.True(Names.Matches("Ann Lee", "Ann Lee"));
        Assert.True(Names.Matches("Ann Lee", "\uE090Ann Lee"));          // party-slot glyph before
        Assert.True(Names.Matches("Ann Lee", "Ann Lee\uE05DCactuar"));    // cross-world glyph + world after
        Assert.True(Names.Matches("Ann Lee", "Ann LeeCactuar"));
        Assert.False(Names.Matches("Ann Lee", "Ann Leez"));
        Assert.False(Names.Matches("Ann Lee", "Joann Lee"));
        Assert.False(Names.Matches("Ann Lee", "Ann Lee-Smith"));
        Assert.False(Names.Matches("", "Ann Lee"));
    }

    [Fact]
    public void NameValidationIsATrustBoundary()
    {
        Assert.True(Names.IsValid("Ann Lee"));
        Assert.True(Names.IsValid("Y'shtola Rhul"));
        Assert.False(Names.IsValid("Ann"));
        Assert.False(Names.IsValid("Ann Lee /shout hi"));
        Assert.False(Names.IsValid("Ann Lee\n/shout"));
        var g = Game("Ann Lee");
        Assert.False(g.Add("ann lee")); // duplicate
    }

    [Fact]
    public void HighestOpeningStartsListIsNeverReordered()
    {
        var g = Game("Ann Lee", "Bo Ray", "Cy Dee");
        g.StartRound();
        Assert.Equal(Phase.Opening, g.Phase);
        Assert.Equal(RollOutcome.OpeningRecorded, g.OnRoll(P(g, "Ann Lee"), 100, null));
        Assert.Equal(RollOutcome.AlreadyRolled, g.OnRoll(P(g, "Ann Lee"), 900, null));
        Assert.Equal(RollOutcome.OpeningWrongMax, g.OnRoll(P(g, "Bo Ray"), 5, 10));
        Assert.Null(P(g, "Bo Ray").Opening);
        Assert.Equal(RollOutcome.OpeningRecorded, g.OnRoll(P(g, "Bo Ray"), 812, 999));
        Assert.Equal(RollOutcome.OrderSet, g.OnRoll(P(g, "Cy Dee"), 400, null));
        Assert.Equal(["Ann Lee", "Bo Ray", "Cy Dee"], g.Players.Select(p => p.Name)); // list untouched
        Assert.Equal("Bo Ray", g.Current!.Name);
        Assert.Equal("Bo Ray", g.Starter!.Name);
        Assert.Null(g.Limit); // the starter rolls a fresh /random, not up to their opening roll
        Assert.False(g.OpeningTie);
        g.OnRoll(P(g, "Bo Ray"), 300, null);
        Assert.Equal("Cy Dee", g.Current!.Name); // then down the list from the starter
        g.OnRoll(P(g, "Cy Dee"), 200, 300);
        Assert.Equal("Ann Lee", g.Current!.Name); // and round to the top
        Assert.Equal("Bo Ray", g.Starter!.Name);
    }

    [Fact]
    public void TurnsLoopAndAlertOnWrongMaxAndOutOfTurn()
    {
        var g = Opened(out _);
        // order Ann (900), Bo (500); Ann starts with a fresh /random
        Assert.Equal(RollOutcome.OutOfTurn, g.OnRoll(P(g, "Bo Ray"), 3, 900));
        Assert.Equal(RollOutcome.WrongMax, g.OnRoll(P(g, "Ann Lee"), 3, 10)); // /random 10 instead of a fresh /random
        Assert.Equal(RollOutcome.Next, g.AcceptPending());
        Assert.Equal(3, g.Limit);
        Assert.Equal(RollOutcome.Next, g.OnRoll(P(g, "Bo Ray"), 3, 3));
        Assert.Equal(RollOutcome.WrongMax, g.OnRoll(P(g, "Ann Lee"), 300, 999));
        Assert.Equal(300, g.Pending!.Value.Value);
        Assert.Equal("Ann Lee", g.Current!.Name); // not advanced
        Assert.Equal(RollOutcome.Next, g.AcceptPending());
        Assert.Null(g.Pending);
        Assert.Equal(300, g.Limit);
        Assert.Equal("Bo Ray", g.Current!.Name);
        Assert.Equal(RollOutcome.Next, g.OnRoll(P(g, "Bo Ray"), 120, 300));
        Assert.Equal("Ann Lee", g.Current!.Name); // looped
        Assert.Equal(RollOutcome.Ignored, g.OnRoll(null, 5, 120)); // spectator
    }

    [Theory]
    [InlineData(1)]
    [InlineData(69)]
    [InlineData(666)]
    public void EndingRollsWinAndPickCostsALife(int ending)
    {
        var g = Opened(out _);
        Assert.Equal(RollOutcome.Won, g.OnRoll(P(g, "Ann Lee"), ending, null));
        Assert.Equal(Phase.Pick, g.Phase);
        Assert.True(g.Pick(P(g, "Bo Ray"), Piece.Shoes));
        Assert.Equal([Piece.Top, Piece.Bottom], P(g, "Bo Ray").Worn);
        Assert.Equal(2, P(g, "Bo Ray").Lives);
        Assert.Equal(1, P(g, "Ann Lee").Wins);
        Assert.Equal(new RoundRecord(1, "Ann Lee", ending, "Bo Ray", Piece.Shoes, 2), g.History.Single());
        Assert.Equal(Phase.Turns, g.Phase); // next round resumes with the player after the winner, no opening roll
        Assert.Equal(2, g.Round);
        Assert.Equal("Bo Ray", g.Current!.Name);
        Assert.Equal(g.Current, g.Starter);
        Assert.Null(g.Limit); // fresh /random
    }

    [Fact]
    public void ThreeLivesThenOutThenGameOver()
    {
        var g = Game("Ann Lee", "Bo Ray", "Cy Dee");
        for (var i = 0; i < 3; i++) WinRound(g, "Ann Lee", "Bo Ray");
        Assert.True(P(g, "Bo Ray").Out);
        Assert.Equal(Phase.Turns, g.Phase);
        Assert.Equal(2, g.Active.Count());
        Assert.Equal(RollOutcome.Ignored, g.OnRoll(P(g, "Bo Ray"), 5, null)); // out players are ignored
        for (var i = 0; i < 3; i++) WinRound(g, "Ann Lee", "Cy Dee");
        Assert.Equal(Phase.GameOver, g.Phase);
        Assert.Equal("Ann Lee", g.Active.Single().Name);
        Assert.Equal(6, g.History.Count);
        Assert.Equal(6, P(g, "Ann Lee").Wins);
        g.NewGame();
        Assert.All(g.Players, p => Assert.Equal(DeathrollGame.AllPieces, p.Worn));
        Assert.Empty(g.History);
        Assert.Empty(g.RollLog);
    }

    [Fact]
    public void OutPlayersAreSkippedInTurns()
    {
        var g = Game("Ann Lee", "Bo Ray", "Cy Dee");
        for (var i = 0; i < 3; i++) WinRound(g, "Ann Lee", "Bo Ray");
        g.StartRound(); // host restart with an opening roll
        g.OnRoll(P(g, "Ann Lee"), 900, null);
        g.OnRoll(P(g, "Cy Dee"), 100, null);
        Assert.Equal("Ann Lee", g.Current!.Name);
        g.OnRoll(P(g, "Ann Lee"), 50, null);
        Assert.Equal("Cy Dee", g.Current!.Name);
        g.OnRoll(P(g, "Cy Dee"), 20, 50);
        Assert.Equal("Ann Lee", g.Current!.Name);
    }

    [Fact]
    public void RemoveAndMoveKeepTheTurnSane()
    {
        var g = Game("Ann Lee", "Bo Ray", "Cy Dee");
        g.StartRound();
        g.OnRoll(P(g, "Ann Lee"), 900, null);
        g.OnRoll(P(g, "Bo Ray"), 500, null);
        Assert.Equal(Phase.Opening, g.Phase);
        g.Remove(P(g, "Cy Dee")); // the last missing opening roll leaves: turns start
        Assert.Equal(Phase.Turns, g.Phase);
        Assert.Equal("Ann Lee", g.Current!.Name);

        g.Add("Cy Dee");
        g.Move(P(g, "Cy Dee"), -1); // Ann, Cy, Bo
        Assert.Equal(["Ann Lee", "Cy Dee", "Bo Ray"], g.Players.Select(p => p.Name));
        g.Remove(P(g, "Ann Lee")); // current leaves: the next in the list is up, same limit
        Assert.Equal("Cy Dee", g.Current!.Name);
        Assert.Null(g.Limit); // whoever starts the turns rolls fresh
        g.Remove(P(g, "Cy Dee"));
        Assert.Equal(Phase.Setup, g.Phase); // one player left cannot play
        g.Move(P(g, "Bo Ray"), -1); // out of range: no-op
    }

    [Fact]
    public void StarterAcceptsBareOr999AndNextRollsToIt()
    {
        foreach (int? max in new int?[] { null, 999 })
        {
            var g = Opened(out _);
            Assert.Equal(RollOutcome.Next, g.OnRoll(P(g, "Ann Lee"), 412, max));
            Assert.Equal(412, g.Limit);
            Assert.Equal("Bo Ray, roll /random 412", Lines.Call(g.Current!.Name, Lines.RollCommand(Venue.Private, false, g.Limit)));
        }

        var r = Game("Ann Lee", "Bo Ray");
        r.StartRound();
        r.OnRoll(P(r, "Ann Lee"), 900, null);
        r.OnRoll(P(r, "Bo Ray"), 10, null);
        r.OnRoll(P(r, "Ann Lee"), 50, null);
        r.StartRound(); // restart: next starter is fresh again
        Assert.Null(r.Limit);
    }

    [Fact]
    public void WinnerMayPickThemselvesAndOnlyAPieceStillWorn()
    {
        var g = Opened(out _);
        g.OnRoll(P(g, "Ann Lee"), 69, null);
        Assert.True(g.Pick(P(g, "Ann Lee"), Piece.Bottom));
        Assert.Equal([Piece.Top, Piece.Shoes], P(g, "Ann Lee").Worn);
        Assert.Equal(new RoundRecord(1, "Ann Lee", 69, "Ann Lee", Piece.Bottom, 2), g.History.Single());
        Assert.Equal(1, P(g, "Ann Lee").Wins);

        Assert.Equal("Bo Ray", g.Current!.Name); // round 2 resumes after the winner
        g.OnRoll(P(g, "Bo Ray"), 5, null);
        g.OnRoll(P(g, "Ann Lee"), 1, 5);
        Assert.False(g.Pick(P(g, "Ann Lee"), Piece.Bottom)); // already off
        Assert.Equal(Phase.Pick, g.Phase);
    }

    [Fact]
    public void WinnerTakingOffTheirLastPieceEndsTheGameForTheOther()
    {
        var g = Game("Ann Lee", "Bo Ray");
        for (var i = 0; i < 3; i++) WinRound(g, "Ann Lee", "Ann Lee");
        Assert.True(P(g, "Ann Lee").Out);
        Assert.Equal(Phase.GameOver, g.Phase);
        Assert.Equal("Bo Ray", g.Active.Single().Name);
    }

    [Fact]
    public void EveryRollOfEveryRoundIsLogged()
    {
        var g = Game("Ann Lee", "Bo Ray");
        g.StartRound();
        g.OnRoll(P(g, "Ann Lee"), 900, null);
        g.OnRoll(P(g, "Ann Lee"), 950, null);      // repeat
        g.OnRoll(P(g, "Bo Ray"), 5, 10);           // wrong opening
        g.OnRoll(P(g, "Bo Ray"), 400, 999);
        g.OnRoll(P(g, "Bo Ray"), 3, null);         // out of turn
        g.OnRoll(P(g, "Ann Lee"), 50, 100);        // wrong number
        g.AcceptPending();
        g.OnRoll(P(g, "Bo Ray"), 30, 40);          // wrong number, then rerolled
        g.OnRoll(P(g, "Bo Ray"), 1, 50);
        g.OnRoll(null, 7, 50);                     // spectator: not logged
        Assert.Equal(
            [RollNote.Opening, RollNote.OpeningRepeat, RollNote.OpeningWrongNumber, RollNote.Opening, RollNote.OutOfTurn,
             RollNote.Accepted, RollNote.WrongNumber, RollNote.Won],
            g.RollLog.Select(r => r.Note));
        Assert.All(g.RollLog, r => Assert.Equal(1, r.Round));
        Assert.Equal(new RollRecord(1, "Bo Ray", 1, 50, RollNote.Won), g.RollLog[^1]);
        g.Pick(P(g, "Ann Lee"));
        g.OnRoll(P(g, "Ann Lee"), 10, null);
        Assert.Equal(2, g.RollLog[^1].Round);
    }

    [Fact]
    public void DummiesAreValidPlayersAndCanBeCleared()
    {
        var g = Game("Ann Lee");
        for (var i = 0; i < DeathrollGame.DummyNames.Length; i++) Assert.True(g.AddDummy());
        Assert.False(g.AddDummy()); // all used
        Assert.All(DeathrollGame.DummyNames, n => Assert.True(Names.IsValid(n)));
        g.StartRound();
        g.RemoveDummies();
        Assert.Equal("Ann Lee", g.Players.Single().Name);
        Assert.False(g.Players.Single().IsDummy);
        Assert.Equal(Phase.Setup, g.Phase); // one player left
    }

    [Fact]
    public void WaitingAndReminderClock()
    {
        var g = Game("Ann Lee", "Bo Ray", "Cy Dee");
        Assert.Null(g.WaitingKey);
        g.StartRound();
        Assert.Equal(["Ann Lee", "Bo Ray", "Cy Dee"], g.Waiting.Select(p => p.Name));
        var clock = new ReminderClock();
        Assert.Null(clock.SecondsLeft(0));                     // no wait yet
        Assert.False(clock.Due(g.WaitingKey, 0, 30_000));      // starts the wait
        Assert.Equal(30, clock.SecondsLeft(0));
        Assert.Equal(1, clock.SecondsLeft(29_500));            // rounded up: never shows 0 before it fires
        Assert.False(clock.Due(g.WaitingKey, 29_999, 30_000));
        Assert.True(clock.Due(g.WaitingKey, 30_000, 30_000));
        Assert.False(clock.Due(g.WaitingKey, 30_001, 30_000)); // not again straight away
        g.OnRoll(P(g, "Ann Lee"), 10, null);                   // progress restarts the wait
        Assert.False(clock.Due(g.WaitingKey, 40_000, 30_000));
        Assert.False(clock.Due(g.WaitingKey, 69_999, 30_000));
        Assert.True(clock.Due(g.WaitingKey, 70_000, 30_000));
        Assert.True(clock.Due(g.WaitingKey, 100_000, 30_000));
        Assert.True(clock.Due(g.WaitingKey, 130_000, 30_000));
        Assert.False(clock.Due(g.WaitingKey, 999_000, 30_000)); // capped at MaxPerTurn
        Assert.Null(clock.SecondsLeft(999_000));                // nothing more coming
        g.OnRoll(P(g, "Bo Ray"), 900, null);
        g.OnRoll(P(g, "Cy Dee"), 20, null);
        Assert.Equal(["Bo Ray"], g.Waiting.Select(p => p.Name));
        var key = g.WaitingKey;
        g.OnRoll(P(g, "Bo Ray"), 5, 10); // wrong number: waiting on the host, not the player
        Assert.Null(g.WaitingKey);
        Assert.Empty(g.Waiting);
        g.AcceptPending();
        Assert.NotEqual(key, g.WaitingKey);
        Assert.Equal(["Cy Dee"], g.Waiting.Select(p => p.Name));
        Assert.False(clock.Due(null, 0, 30_000));
        Assert.False(clock.Due(null, 999_999, 30_000)); // no wait, no reminder
    }

    [Fact]
    public void OpeningTieIsFlagged()
    {
        var g = Game("Ann Lee", "Bo Ray");
        g.StartRound();
        g.OnRoll(P(g, "Ann Lee"), 500, null);
        g.OnRoll(P(g, "Bo Ray"), 500, null);
        Assert.True(g.OpeningTie);
        Assert.Equal("Ann Lee", g.Current!.Name); // list order breaks the tie
    }

    [Fact]
    public void LinesAndCommands()
    {
        Assert.Equal("/random", Lines.RollCommand(Venue.Private, false, null));
        Assert.Equal("/random 532", Lines.RollCommand(Venue.Private, true, 532));
        Assert.Equal("/dice party 532", Lines.RollCommand(Venue.Public, false, 532));
        Assert.Equal("/dice alliance", Lines.RollCommand(Venue.Public, true, null));
        Assert.Equal("/s", Lines.AnnouncePrefix(Venue.Private, true));
        Assert.Equal("/p", Lines.AnnouncePrefix(Venue.Public, false));
        Assert.Equal("/a", Lines.AnnouncePrefix(Venue.Public, true));
        Assert.Equal("/y", Lines.TextPrefix(TextChannel.Yell));
        Assert.Equal("Ann Lee, roll /random 532", Lines.Call("Ann Lee", "/random 532"));
        Assert.Equal("Bo Ray loses their top! 1 piece left.", Lines.Stripped("Bo Ray", Piece.Top, 1));
        Assert.Equal("Bo Ray loses their shoes, the last piece, and is out!", Lines.Stripped("Bo Ray", Piece.Shoes, 0));
        Assert.Equal("/s", Lines.CallPrefix(CallChannel.Venue, Venue.Private, true));
        Assert.Equal("/a", Lines.CallPrefix(CallChannel.Venue, Venue.Public, true));
        Assert.Equal("/y", Lines.CallPrefix(CallChannel.Yell, Venue.Public, true));
        Assert.Equal("/s", Lines.CallPrefix(CallChannel.Say, Venue.Public, false));
        Assert.Equal("/p", Lines.CallPrefix(CallChannel.Party, Venue.Private, false));
        Assert.Equal("Reminder: Ann Lee, it's your turn - roll /random 40", Lines.Reminder(["Ann Lee"], "/random 40", false));
        Assert.Equal("Still waiting on Ann Lee, Bo Ray: roll /random to start the round.", Lines.Reminder(["Ann Lee", "Bo Ray"], "/random", true));
        var many = Enumerable.Range(0, 20).Select(i => "Abcdefghijklmno Abcdefghijklmno").ToList();
        Assert.True(Lines.IsSendable(Lines.Reminder(many, "/dice alliance", true))); // capped at 6 names
        Assert.False(Lines.IsSendable("/shout hi"));
        Assert.False(Lines.IsSendable("a\tb"));
        Assert.False(Lines.IsSendable(new string('x', 401)));
        Assert.True(Lines.IsSendable("ストリップ"));
        Assert.Equal(["a", "b"], Lines.TextLines("a\r\n\r\n b \n"));
        Assert.Equal(Lines.MaxTextLines, Lines.TextLines(string.Join('\n', Enumerable.Range(0, 50))).Count);
    }

    [Fact]
    public void EnumNumbersArePinned()
    {
        Assert.Equal(0, (int)Venue.Private);
        Assert.Equal(1, (int)Venue.Public);
        Assert.Equal([0, 1, 2], new[] { TextChannel.Say, TextChannel.Yell, TextChannel.Party }.Select(c => (int)c));
        Assert.Equal([0, 1, 2, 3], new[] { CallChannel.Venue, CallChannel.Say, CallChannel.Yell, CallChannel.Party }.Select(c => (int)c));
    }

    // A round can never ask for rolls forever: each accepted roll is <= the limit, a 1 always ends it.
    [Fact]
    public void RandomPlaySweepAlwaysFinishes()
    {
        var rng = new Random(1234);
        for (var game = 0; game < 2000; game++)
        {
            var g = Game("Ann Lee", "Bo Ray", "Cy Dee", "Di Fay");
            var rolls = 0;
            while (g.Phase != Phase.GameOver)
            {
                if (g.Phase == Phase.Setup) g.StartRound();
                if (g.Phase == Phase.Opening)
                    foreach (var p in g.Active.Where(p => p.Opening is null).ToList()) g.OnRoll(p, rng.Next(1, 1000), null);
                else if (g.Phase == Phase.Turns)
                {
                    var out1 = g.OnRoll(g.Current, rng.Next(1, (g.Limit ?? DeathrollGame.OpeningMax) + 1), g.Limit);
                    Assert.True(out1 is RollOutcome.Next or RollOutcome.Won);
                }
                else if (g.Phase == Phase.Pick)
                {
                    var choices = g.Active.ToList(); // the winner may pick themselves
                    var target = choices[rng.Next(choices.Count)];
                    Assert.True(g.Pick(target, target.Worn[rng.Next(target.Worn.Count)]));
                }

                Assert.True(++rolls < 20_000, "game did not finish");
            }

            var survivor = Assert.Single(g.Active);
            Assert.Equal(9 + (DeathrollGame.StartLives - survivor.Lives), g.History.Count); // 3 losers x 3 pieces + the survivor's
        }
    }

    private static DeathrollGame Opened(out DeathrollGame g)
    {
        g = Game("Ann Lee", "Bo Ray");
        g.StartRound();
        g.OnRoll(P(g, "Bo Ray"), 500, null);
        g.OnRoll(P(g, "Ann Lee"), 900, null);
        return g;
    }

    [Fact]
    public void LaterRoundsResumeAfterTheWinnerSkippingOutPlayers()
    {
        var g = Game("Ann Lee", "Bo Ray", "Cy Dee");
        g.StartRound();
        g.OnRoll(P(g, "Ann Lee"), 100, null);
        g.OnRoll(P(g, "Bo Ray"), 900, null);
        g.OnRoll(P(g, "Cy Dee"), 200, null);
        Assert.Equal(RollOutcome.Won, g.OnRoll(P(g, "Bo Ray"), 1, null)); // Bo starts and wins at once
        Assert.True(g.Pick(P(g, "Ann Lee")));
        Assert.Equal("Cy Dee", g.Current!.Name);                          // next after Bo
        Assert.Equal(RollOutcome.Won, g.OnRoll(P(g, "Cy Dee"), 69, null));
        Assert.True(g.Pick(P(g, "Ann Lee")));
        Assert.Equal("Ann Lee", g.Current!.Name);                         // wraps round
        Assert.False(g.Pick(P(g, "Ann Lee")));                             // turns, not pick
        Assert.Equal(RollOutcome.Won, g.OnRoll(P(g, "Ann Lee"), 1, null));
        Assert.True(g.Pick(P(g, "Bo Ray")));
        Assert.Equal("Bo Ray", g.Current!.Name);
        Assert.Equal(RollOutcome.Won, g.OnRoll(P(g, "Bo Ray"), 1, null));
        Assert.True(g.Pick(P(g, "Cy Dee")));
        Assert.Equal(RollOutcome.Won, g.OnRoll(P(g, "Cy Dee"), 1, null));
        Assert.True(g.Pick(P(g, "Ann Lee")));                              // Ann's last piece: out
        Assert.True(P(g, "Ann Lee").Out);
        Assert.Equal("Bo Ray", g.Current!.Name);                          // after Cy, Ann is skipped
    }

    [Fact]
    public void WinnerWhoStripsThemselvesOutDoesNotBlockTheNextStarter()
    {
        var g = Game("Ann Lee", "Bo Ray", "Cy Dee");
        for (var i = 0; i < 2; i++) WinRound(g, "Ann Lee", "Ann Lee");
        g.StartRound();
        g.OnRoll(P(g, "Ann Lee"), 900, null);
        g.OnRoll(P(g, "Bo Ray"), 10, null);
        g.OnRoll(P(g, "Cy Dee"), 20, null);
        Assert.Equal(RollOutcome.Won, g.OnRoll(P(g, "Ann Lee"), 1, null));
        Assert.True(g.Pick(P(g, "Ann Lee")));      // her last piece: out
        Assert.True(P(g, "Ann Lee").Out);
        Assert.Equal("Bo Ray", g.Current!.Name);   // next after the (now out) winner
        Assert.Equal(RollOutcome.Ignored, g.OnRoll(P(g, "Ann Lee"), 5, null));
    }

    private static void WinRound(DeathrollGame g, string winner, string target)
    {
        if (g.Phase != Phase.Opening) g.StartRound();
        foreach (var p in g.Active.ToList()) g.OnRoll(p, p.Name == winner ? 999 : 10, null);
        Assert.Equal(winner, g.Current!.Name);
        Assert.Equal(RollOutcome.Won, g.OnRoll(g.Current, 1, g.Limit));
        Assert.True(g.Pick(P(g, target)));
    }

    private static void WinRoundNoPick(DeathrollGame g, string winner)
    {
        if (g.Phase != Phase.Opening) g.StartRound();
        foreach (var p in g.Active.ToList()) g.OnRoll(p, p.Name == winner ? 999 : 10, null);
        Assert.Equal(RollOutcome.Won, g.OnRoll(g.Current, 1, g.Limit));
    }
}
