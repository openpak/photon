using System;
using BlackIce.Server.Common;
using Xunit;

namespace BlackIce.Server.Data.Tests;

public class ResultTests
{
    // --- Result<T> success / failure -------------------------------------------------------------

    [Fact]
    public void Value_result_success_carries_value()
    {
        Result<int> r = 42;                       // implicit success
        Assert.True(r.IsOk);
        Assert.False(r.IsFail);
        Assert.Equal(42, r.Value);
        Assert.Equal(42, r.ValueOr(-1));
        Assert.True(r.TryGet(out var v));
        Assert.Equal(42, v);
    }

    [Fact]
    public void Value_result_failure_has_code_and_no_value()
    {
        Result<int> r = Result.Fail(ErrorCode.NotFound);   // implicit failure via Err
        Assert.True(r.IsFail);
        Assert.Equal(ErrorCode.NotFound, r.Error);
        Assert.Equal(-1, r.ValueOr(-1));
        Assert.False(r.TryGet(out _));
        Assert.Throws<InvalidOperationException>(() => _ = r.Value);
    }

    // --- Status (Unit) result --------------------------------------------------------------------

    [Fact]
    public void Status_result_ok_and_fail()
    {
        Assert.True(Result.Ok.IsOk);
        Result fail = Result.Fail(ErrorCode.PermissionDenied);
        Assert.True(fail.IsFail);
        Assert.Equal(ErrorCode.PermissionDenied, fail.Error);
    }

    // --- Source-location capture -----------------------------------------------------------------

    [Fact]
    public void Failure_captures_origin_at_the_fail_call_site()
    {
        var r = MakeFailure();
        Assert.True(r.Location.IsKnown);
        Assert.Equal(nameof(MakeFailure), r.Location.Member);
        Assert.Contains("ResultTests", r.Location.File);
    }

    private static Result MakeFailure() => Result.Fail(ErrorCode.BadState);

    // --- Map / Bind (railway propagation) --------------------------------------------------------

    [Fact]
    public void Map_transforms_success_and_propagates_failure()
    {
        Assert.Equal(10, ((Result<int>)5).Map(x => x * 2).Value);

        var propagated = ((Result<int>)Result.Fail(ErrorCode.Corrupt)).Map(x => x * 2);
        Assert.True(propagated.IsFail);
        Assert.Equal(ErrorCode.Corrupt, propagated.Error);
    }

    [Fact]
    public void Bind_chains_and_short_circuits_on_failure()
    {
        Result<int> Halve(int n) => n % 2 == 0 ? n / 2 : Result.Fail(ErrorCode.InvalidArgument);

        Assert.Equal(4, ((Result<int>)8).Bind(Halve).Value);
        Assert.True(((Result<int>)7).Bind(Halve).IsFail);
    }

    [Fact]
    public void ToStatus_and_ToError_round_trip_the_code()
    {
        Assert.True(((Result<int>)1).ToStatus().IsOk);
        Result status = Result.Fail(ErrorCode.Busy);
        Result<string> asValue = status.ToError<string>();
        Assert.True(asValue.IsFail);
        Assert.Equal(ErrorCode.Busy, asValue.Error);
    }

    [Fact]
    public void Match_selects_the_right_branch()
    {
        Assert.Equal("ok:3", ((Result<int>)3).Match(v => $"ok:{v}", e => $"err:{e.Code}"));
        Assert.Equal("err:Timeout", ((Result<int>)Result.Fail(ErrorCode.Timeout)).Match(v => $"ok:{v}", e => $"err:{e.Code}"));
    }

    // --- Policies --------------------------------------------------------------------------------

    [Fact]
    public void LogAndDrop_logs_on_failure_only()
    {
        var prevWarn = ResultDiagnostics.Warn;
        try
        {
            string? logged = null;
            ResultDiagnostics.Warn = (_, msg) => logged = msg;

            Result.Ok.LogAndDrop("test", "should-not-log");
            Assert.Null(logged);

            Result failed = Result.Fail(ErrorCode.IoError);
            failed.LogAndDrop("test", "io-step");
            Assert.NotNull(logged);
            Assert.Contains("io-step", logged);
            Assert.Contains("IoError", logged);
        }
        finally { ResultDiagnostics.Warn = prevWarn; }
    }

    [Fact]
    public void Expect_returns_value_on_success_and_throws_on_failure()
    {
        var prevError = ResultDiagnostics.Error;
        try
        {
            ResultDiagnostics.Error = (_, _) => { };   // silence
            Assert.Equal(99, ((Result<int>)99).Expect("must have value"));
            Result bad = Result.Fail(ErrorCode.BadState);
            var ex = Assert.Throws<ResultExpectException>(() => bad.Expect("invariant"));
            Assert.Equal(ErrorCode.BadState, ex.Code);
        }
        finally { ResultDiagnostics.Error = prevError; }
    }

    // --- Defer / ScopeGuard ----------------------------------------------------------------------

    [Fact]
    public void Defer_runs_cleanup_on_scope_exit()
    {
        var ran = false;
        using (Defer.Run(() => ran = true)) { }
        Assert.True(ran);
    }

    [Fact]
    public void Dismissed_defer_does_not_run()
    {
        var ran = false;
        using (var g = Defer.Run(() => ran = true)) { g.Dismiss(); }
        Assert.False(ran);
    }

    [Fact]
    public void Defer_releases_only_on_early_failure()
    {
        // Models the acquire-then-maybe-fail pattern: cleanup runs when we bail, not when we hand off.
        // `released` is read AFTER the guarded scope exits, so it reflects whether the guard fired.
        static bool RunStep(bool succeed, out bool released)
        {
            var rel = false;
            bool ok = Inner();
            released = rel;
            return ok;

            bool Inner()
            {
                using var guard = Defer.Run(() => rel = true);
                if (!succeed) return false;   // guard fires on Inner's scope exit
                guard.Dismiss();              // success: hand the resource off
                return true;
            }
        }

        Assert.True(RunStep(succeed: true, out var releasedOnSuccess));
        Assert.False(releasedOnSuccess);      // handed off, not released

        Assert.False(RunStep(succeed: false, out var releasedOnFailure));
        Assert.True(releasedOnFailure);       // bailed, released
    }
}
