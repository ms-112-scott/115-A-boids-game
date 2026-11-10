using System;
using System.Collections.Generic;
using System.Drawing;
using Rhino.Geometry;

namespace BoidArena
{
    // ============================================================================
    // 隊伍：chialing v2
    // 性格：獨行俠、搶食物與五芒星、不讓敵人繞到背後、以「長大」為主，攻擊只是順手
    //
    // 行為優先順序（全部加權混合成一個想去的方向）：
    //   1. 背後有敵人正衝過來 → 往敵人行進方向的側邊急轉（讓牠撞到我的側面＝只會彈開）
    //   2. 避開牆壁與障礙物（縮小期多留一點空間）
    //   3. 搶五芒星、搶食物（隊友比較近的就讓給隊友，不跟隊友搶同一顆）
    //   4. 跟隊友保持距離，不成群
    //   5. 剛好有敵人背對我、又很近 → 順手撞牠的背
    //   6. 沒事做就隨機漫遊，太靠邊緣時往中間飄
    // ============================================================================
    public class Boid_chialing : IBrain, IDeclareRelations
    {
        public string Name { get { return "chialing"; } }

        // ---- 可調參數（static readonly 是允許的）----
        static readonly double ThreatRange = 8.0;      // 多近的背後敵人算威脅
        static readonly double ThreatBehind = -0.1;    // 敵人在我後半邊（含一點側邊）就算
        static readonly double ThreatFacing = 0.5;     // 牠的頭要朝著我
        static readonly double SideStepRange = 2.5;    // 這麼近就改成往側邊閃
        static readonly double RunWeight = 1.0;
        static readonly double AllySpaceRange = 4.5;   // 跟隊友保持的距離
        static readonly double WallMargin = 3.5;       // 離牆多近開始轉回
        static readonly double ObstacleMargin = 2.5;   // 離障礙物多近開始閃
        static readonly double AmbushRange = 6.0;      // 順手偷襲的距離

        static readonly double WThreat = 4.0;
        static readonly double WWall = 2.5;
        static readonly double WObstacle = 3.0;
        static readonly double WTarget = 5.0;
        static readonly double WAllySpace = 0.7;
        static readonly double WAmbush = 2.0;
        static readonly double WWander = 0.4;

        // 預設就好：同隊友軍、異隊敵軍（擊殺規則不看標籤，所以不特別改）
        public void Declare(RelationTable table)
        {
        }

        public Vector3d Decide(SelfInfo self, List<Neighbor> neighbors, List<Point3d> foods, World world)
        {
            try
            {
                Vector3d result = Think(self, neighbors, foods, world);
                result.Z = 0;
                if (double.IsNaN(result.X) || double.IsNaN(result.Y) ||
                    double.IsInfinity(result.X) || double.IsInfinity(result.Y))
                    return Vector3d.Zero;
                return result;
            }
            catch
            {
                // 萬一出錯也不要丟例外（丟例外整隊淘汰）
                return Vector3d.Zero;
            }
        }

        Vector3d Think(SelfInfo self, List<Neighbor> neighbors, List<Point3d> foods, World world)
        {
            if (neighbors == null) neighbors = new List<Neighbor>();
            if (foods == null) foods = new List<Point3d>();
            List<Point3d> stars = self.Stars ?? new List<Point3d>();

            Point3d me = new Point3d(self.Position.X, self.Position.Y, 0);
            Vector3d vel = new Vector3d(self.Velocity.X, self.Velocity.Y, 0);
            Vector3d dir = Steering.Unit(vel);
            if (dir.Length == 0) dir = new Vector3d(1, 0, 0);

            bool invincible = self.InvincibleSteps > 0;
            Vector3d desired = Vector3d.Zero;

            // ---------- 1. 背後威脅：急轉到側邊 ----------
            Vector3d escape = Vector3d.Zero;
            if (!invincible)
            {
                foreach (Neighbor n in neighbors)
                {
                    if (n.IsFriend) continue;
                    double d = n.Distance;
                    if (d < 1e-6 || d > ThreatRange) continue;

                    Vector3d toEnemy = new Vector3d(n.Position.X - me.X, n.Position.Y - me.Y, 0) / d;
                    Vector3d enemyDir = Steering.Unit(new Vector3d(n.Velocity.X, n.Velocity.Y, 0));
                    if (enemyDir.Length == 0) continue;

                    double behindMe = -(toEnemy * dir);      // > 0 表示敵人在我後方
                    double facingMe = enemyDir * (-toEnemy);  // > 0 表示牠的頭朝著我
                    if (behindMe < ThreatBehind || facingMe < ThreatFacing) continue;

                    // 往「垂直於敵人前進方向」的那一側閃開，牠就只能撞到我的側面
                    Vector3d perp = new Vector3d(-enemyDir.Y, enemyDir.X, 0);
                    if (perp * (-toEnemy) < 0) perp = -perp;   // 選離牠前進線比較遠的那一側

                    double urgency = (ThreatRange - d) / ThreatRange;
                    urgency *= urgency;
                    urgency *= (0.5 + facingMe);               // 越正對我越危險

                    // 還遠：直直往遠離牠的方向跑（速度一樣，牠追不上）
                    // 很近：往側邊閃，讓牠撞到側面只會彈開
                    Vector3d run = -toEnemy;
                    double close = d < SideStepRange ? 1.0 : 0.0;
                    escape += (perp * (0.4 + close) + run * (1.0 - close) * RunWeight + dir * 0.25) * urgency;
                }
            }
            desired += escape * WThreat;

            // ---------- 2. 牆壁 ----------
            double margin = WallMargin + (world.Phase == 2 ? 1.5 : 0);   // 縮小期多留空間
            double h = world.HalfSize;
            Vector3d wall = Vector3d.Zero;
            if (me.X > h - margin) wall.X -= (me.X - (h - margin)) / margin;
            if (me.X < -h + margin) wall.X += ((-h + margin) - me.X) / margin;
            if (me.Y > h - margin) wall.Y -= (me.Y - (h - margin)) / margin;
            if (me.Y < -h + margin) wall.Y += ((-h + margin) - me.Y) / margin;
            desired += wall * WWall;

            // ---------- 2b. 障礙物 ----------
            Vector3d avoid = Vector3d.Zero;
            if (world.Obstacles != null)
            {
                foreach (Obstacle o in world.Obstacles)
                {
                    if (o == null) continue;
                    double d = o.DistanceTo(me);
                    if (d >= ObstacleMargin) continue;
                    Vector3d outward = d < 1e-6
                        ? Steering.Unit(new Vector3d(me.X - o.Center.X, me.Y - o.Center.Y, 0))
                        : Steering.Unit(me - o.ClosestPoint(me));
                    outward.Z = 0;
                    if (outward * dir > 0.3) continue;   // 已經在遠離就不管
                    double k = (ObstacleMargin - d) / ObstacleMargin;
                    // 往外推 + 沿著邊滑開（比較不會卡住）
                    Vector3d slide = new Vector3d(-outward.Y, outward.X, 0);
                    if (slide * dir < 0) slide = -slide;
                    avoid += (outward + slide * 0.6) * k;
                }
            }
            desired += avoid * WObstacle;

            // ---------- 3. 搶五芒星與食物 ----------
            Point3d target = Point3d.Unset;
            double bestCost = double.MaxValue;

            foreach (Point3d s in stars)
            {
                double d = me.DistanceTo(s);
                // 五芒星價值高；已經無敵的話讓給隊友
                double cost = d * (invincible ? 1.5 : 0.4) + ContestPenalty(s, d, neighbors) + BehindPenalty(me, dir, s, d);
                if (cost < bestCost) { bestCost = cost; target = s; }
            }
            foreach (Point3d f in foods)
            {
                double d = me.DistanceTo(f);
                double cost = d + ContestPenalty(f, d, neighbors) + BehindPenalty(me, dir, f, d);
                if (cost < bestCost) { bestCost = cost; target = f; }
            }
            bool hasTarget = target.IsValid;
            if (hasTarget)
            {
                Vector3d toTarget = new Vector3d(target.X - me.X, target.Y - me.Y, 0);
                // 有威脅時，目標的權重降一點，先保命
                double w = WTarget * (escape.Length > 0.3 ? 0.4 : 1.0);
                desired += Steering.Unit(toTarget) * w;
            }

            // ---------- 4. 和隊友保持距離（不成群） ----------
            Vector3d space = Vector3d.Zero;
            foreach (Neighbor n in neighbors)
            {
                if (!n.IsFriend) continue;
                double d = n.Distance;
                if (d < 1e-6 || d > AllySpaceRange) continue;
                Vector3d away = new Vector3d(me.X - n.Position.X, me.Y - n.Position.Y, 0) / d;
                space += away * ((AllySpaceRange - d) / AllySpaceRange);
            }
            desired += Steering.Unit(space) * Math.Min(space.Length, 1.0) * WAllySpace;

            // ---------- 5. 順手偷襲：敵人在我前方、而且背對著我 ----------
            if (escape.Length < 0.1)
            {
                double bestPrey = double.MaxValue;
                Vector3d preyDir = Vector3d.Zero;
                foreach (Neighbor n in neighbors)
                {
                    if (n.IsFriend || n.InvincibleSteps > 0) continue;   // 無敵的殺不死，不追
                    double d = n.Distance;
                    if (d < 1e-6 || d > AmbushRange) continue;
                    Vector3d toEnemy = new Vector3d(n.Position.X - me.X, n.Position.Y - me.Y, 0) / d;
                    Vector3d enemyDir = Steering.Unit(new Vector3d(n.Velocity.X, n.Velocity.Y, 0));
                    if (toEnemy * dir < 0.6) continue;          // 要在我前方
                    if (enemyDir.Length > 0 && enemyDir * toEnemy < 0.5) continue;   // 我要在牠後方
                    if (d < bestPrey)
                    {
                        bestPrey = d;
                        // 瞄準牠下一點會到的位置
                        Point3d future = n.Position + new Vector3d(n.Velocity.X, n.Velocity.Y, 0) * 2.0;
                        preyDir = Steering.Unit(new Vector3d(future.X - me.X, future.Y - me.Y, 0));
                    }
                }
                // 無敵時更積極一點（反正殺不死我）
                desired += preyDir * WAmbush * (invincible ? 1.5 : 1.0) * (hasTarget ? 0.6 : 1.0);
            }

            // ---------- 6. 漫遊 ----------
            if (!hasTarget)
            {
                double jitter = self.Rng != null ? (self.Rng.NextDouble() * 2 - 1) * 0.6 : 0;
                Vector3d side = new Vector3d(-dir.Y, dir.X, 0);
                Vector3d wander = dir + side * jitter;

                // 太靠外圈就往中間飄（食物通常不在角落，縮圈時中間也比較安全）
                Vector3d toCenter = new Vector3d(world.Center.X - me.X, world.Center.Y - me.Y, 0);
                double far = toCenter.Length / Math.Max(h, 1e-6);
                if (far > 0.5) wander += Steering.Unit(toCenter) * (far - 0.5) * 1.5;

                // 決戰期：沒食物了，往敵人少的方向晃
                if (world.Phase == 3)
                {
                    Vector3d calm = Vector3d.Zero;
                    foreach (Neighbor n in neighbors)
                    {
                        if (n.IsFriend || n.Distance < 1e-6) continue;
                        calm += new Vector3d(me.X - n.Position.X, me.Y - n.Position.Y, 0) / (n.Distance * n.Distance);
                    }
                    wander += Steering.Unit(calm) * 0.8;
                }
                desired += Steering.Unit(wander) * WWander;
            }

            desired.Z = 0;
            if (desired.Length < 1e-9) return Vector3d.Zero;
            return Steering.Seek(desired, vel, world);
        }

        // 隊友比我更近 → 讓給隊友；敵人守在旁邊 → 危險
        static double ContestPenalty(Point3d p, double myDist, List<Neighbor> neighbors)
        {
            double penalty = 0;
            foreach (Neighbor n in neighbors)
            {
                double d = n.Position.DistanceTo(p);
                if (n.IsFriend)
                {
                    if (d < myDist - 0.5) { penalty += 5.0; break; }
                }
                else if (d < 2.0)
                {
                    penalty += 2.0;
                }
            }
            return penalty;
        }

        // 在我正後方的東西要大迴轉才吃得到（轉彎半徑約 5），稍微扣分
        static double BehindPenalty(Point3d me, Vector3d dir, Point3d p, double d)
        {
            if (d < 1e-6) return 0;
            Vector3d to = new Vector3d(p.X - me.X, p.Y - me.Y, 0) / d;
            double c = to * dir;   // 1 = 正前方，-1 = 正後方
            return (1 - c) * 1.5;
        }
    }
}
