using System;
using System.Collections.Generic;
using Game.Domain.AI.Coordination;
using NUnit.Framework;
using ZFrame.Gameplay;

namespace Game.Domain.Tests
{
    /// <summary>
    /// TaskBoard tests (TASK-A-AI-02) — the falsifiable core of the task
    /// layer, all hand-computable:
    /// - 认领互斥: two claimants race one task, exactly one wins; even the
    ///   holder itself cannot "re-claim" (a claim is an acquisition, not an
    ///   assertion);
    /// - 到期回收: an expired task is unclaimable immediately and swept away
    ///   by <see cref="TaskBoard.SweepExpired"/> — including a stale CLAIM;
    /// - holder-only Complete/Abandon, publish-order listing, board-issued ids.
    /// </summary>
    public sealed class TaskBoardTests
    {
        // ---- 发布：板发 id、计数、列表顺序 ----

        [Test]
        public void Publish_IssuesStableIds_InPublishOrder()
        {
            var board = new TaskBoard();
            CoordinationTask first = board.Publish("pickup", new Vec2(1f, 0f), float.PositiveInfinity);
            CoordinationTask second = board.Publish("repair", new Vec2(2f, 0f), float.PositiveInfinity);

            Assert.AreEqual("task-1", first.Id, "板发 id 从 task-1 单调递增");
            Assert.AreEqual("task-2", second.Id);
            Assert.AreEqual(2, board.Count);
            Assert.AreEqual(2, board.PublishedCount);
            Assert.AreEqual(2, board.OpenTasks().Count, "两个都开放");
            Assert.AreEqual("task-1", board.OpenTasks()[0].Id, "开放任务按发布序");
            Assert.AreEqual(CoordinationTaskStatus.Open, first.Status);
            Assert.AreEqual(string.Empty, first.ClaimedBy, "未认领时无持有人");
        }

        [Test]
        public void Publish_RejectsEmptyKindAndBadExpiry()
        {
            var board = new TaskBoard();
            Assert.Throws<ArgumentException>(() => board.Publish("", new Vec2(1f, 0f), 10f));
            Assert.Throws<ArgumentOutOfRangeException>(() => board.Publish("pickup", new Vec2(1f, 0f), 0f));
            Assert.Throws<ArgumentOutOfRangeException>(() => board.Publish("pickup", new Vec2(1f, 0f), float.NaN));
            Assert.DoesNotThrow(() => board.Publish("pickup", new Vec2(1f, 0f), float.PositiveInfinity), "+Infinity = 永不过期");
        }

        // ---- 核心证据：认领互斥 ----

        [Test]
        public void Claim_TwoClaimantsRace_ExactlyOneWins()
        {
            var board = new TaskBoard();
            board.Publish("pickup", new Vec2(3f, 4f), 30f);

            Assert.IsTrue(board.Claim("task-1", "npc-a", 1f), "先到者认领成功");
            Assert.IsFalse(board.Claim("task-1", "npc-b", 1f), "认领互斥：第二人失败");

            board.TryGetTask("task-1", out CoordinationTask task);
            Assert.AreEqual(CoordinationTaskStatus.Claimed, task.Status);
            Assert.AreEqual("npc-a", task.ClaimedBy, "持有人 = 胜者");
            Assert.AreEqual(1f, task.ClaimedAt, 1e-5f);
            Assert.AreEqual(0, board.OpenTasks().Count, "唯一任务被认领后开放池清空（被认领任务不再出现在开放池）");
        }

        [Test]
        public void Claim_SameClaimantReclaim_IsNotAnAcquisition()
        {
            var board = new TaskBoard();
            board.Publish("pickup", new Vec2(3f, 4f), 30f);
            Assert.IsTrue(board.Claim("task-1", "npc-a", 1f));
            Assert.IsFalse(board.Claim("task-1", "npc-a", 2f), "已持有者再认领不算新认领（返回 false，而非刷新时间戳）");
        }

        [Test]
        public void Claim_UnknownTask_Fails()
        {
            var board = new TaskBoard();
            Assert.IsFalse(board.Claim("task-404", "npc-a", 1f));
            Assert.IsFalse(board.Claim(null, "npc-a", 1f));
        }

        [Test]
        public void Claim_RequiresClaimantIdentity()
        {
            var board = new TaskBoard();
            board.Publish("pickup", new Vec2(1f, 0f), 10f);
            Assert.Throws<ArgumentException>(() => board.Claim("task-1", "", 1f));
            Assert.Throws<ArgumentException>(() => board.Claim("task-1", null, 1f));
        }

        // ---- 到期语义 ----

        [Test]
        public void Claim_AtExpiryInstant_Fails()
        {
            var board = new TaskBoard();
            board.Publish("pickup", new Vec2(1f, 0f), 5f);

            Assert.IsTrue(board.Claim("task-1", "npc-a", 4.999f), "到期前一刻仍可认领");
            board.Abandon("task-1", "npc-a");
            Assert.IsFalse(board.Claim("task-1", "npc-b", 5f), "到期即失效（now >= ExpiresAt）");
        }

        // ---- 核心证据：到期回收 ----

        [Test]
        public void SweepExpired_RemovesExpiredTask_ReleasesStaleClaim()
        {
            var board = new TaskBoard();
            board.Publish("pickup", new Vec2(1f, 0f), 5f);   // task-1：会到期
            board.Publish("repair", new Vec2(2f, 0f), float.PositiveInfinity); // task-2：永不
            Assert.IsTrue(board.Claim("task-1", "npc-a", 1f), "先认领，制造一个陈旧 claim");

            IReadOnlyList<CoordinationTask> swept = board.SweepExpired(10f);

            Assert.AreEqual(1, swept.Count, "只有 task-1 被回收");
            Assert.AreEqual("task-1", swept[0].Id);
            Assert.AreEqual(1, board.Count, "task-2 仍在板上");
            Assert.IsFalse(board.TryGetTask("task-1", out _), "被回收任务不再可查");
            Assert.IsFalse(board.Claim("task-1", "npc-b", 10f), "回收后认领必失败");
        }

        [Test]
        public void SweepExpired_EmptyBoardOrNothingExpired_ReturnsEmpty()
        {
            var board = new TaskBoard();
            Assert.AreEqual(0, board.SweepExpired(10f).Count, "空板回收为空");
            board.Publish("pickup", new Vec2(1f, 0f), 100f);
            Assert.AreEqual(0, board.SweepExpired(10f).Count, "未到期不回收");
            Assert.AreEqual(1, board.Count, "任务未被误删");
        }

        // ---- 完成/放生：仅持有人 ----

        [Test]
        public void Complete_HolderOnly_RemovesTaskFromBoard()
        {
            var board = new TaskBoard();
            board.Publish("pickup", new Vec2(1f, 0f), 30f);
            board.Claim("task-1", "npc-a", 1f);

            Assert.IsFalse(board.Complete("task-1", "npc-b"), "非持有人不能完成");
            Assert.IsTrue(board.Complete("task-1", "npc-a"), "持有人完成");
            Assert.AreEqual(0, board.Count, "完成任务离开板");
            Assert.IsFalse(board.TryGetTask("task-1", out _));
            Assert.IsFalse(board.Complete("task-1", "npc-a"), "已完成任务不能重复完成");
        }

        [Test]
        public void Abandon_HolderReleases_TaskBackToOpen()
        {
            var board = new TaskBoard();
            board.Publish("pickup", new Vec2(1f, 0f), 30f);
            board.Claim("task-1", "npc-a", 1f);

            Assert.IsFalse(board.Abandon("task-1", "npc-b"), "非持有人不能放生");
            Assert.IsTrue(board.Abandon("task-1", "npc-a"));
            board.TryGetTask("task-1", out CoordinationTask task);
            Assert.AreEqual(CoordinationTaskStatus.Open, task.Status, "放生后回到开放");
            Assert.AreEqual(string.Empty, task.ClaimedBy);
            Assert.IsTrue(board.Claim("task-1", "npc-b", 2f), "别人可以接手");
        }

        [Test]
        public void AllTasks_IncludesClaimedInPublishOrder()
        {
            var board = new TaskBoard();
            board.Publish("pickup", new Vec2(1f, 0f), 30f);
            board.Publish("pickup", new Vec2(2f, 0f), 30f);
            board.Claim("task-2", "npc-a", 1f);

            List<CoordinationTask> all = board.AllTasks();
            Assert.AreEqual(2, all.Count);
            Assert.AreEqual("task-1", all[0].Id);
            Assert.AreEqual("task-2", all[1].Id);
            Assert.AreEqual("npc-a", all[1].ClaimedBy);
        }
    }
}
