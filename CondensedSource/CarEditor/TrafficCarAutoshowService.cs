using System;
using System.Collections.Generic;
using System.Linq;
using FH6LocalCryptoTool;
using Microsoft.Data.Sqlite;

namespace FH6CarEditor;

public sealed record TrafficCarBaseline(
    long CarId,
    string MediaName,
    bool IsFromStockRef,
    bool WasUnlockedAtCapture,
    bool WasInUnobtainable,
    long? BucketId,
    long? BucketHero,
    bool HasOnDiscContent,
    long? ContentId,
    long? NotAvailableInAutoshow,
    long? VisibleOnlyIfOwned,
    long? NotAvailableInAuctionHouse,
    long? IsDrivable,
    long? IsPurchased,
    long? IsInstalled,
    long? IsUnicorn,
    long? IsArcade,
    long? AllIeIncludeCar,
    long? AllIeIncludeAudio);

public sealed record TrafficCarUnlockResult(int TotalTrafficCars, int NewlyUnlocked, int AddedBuckets, int ClearedUnobtainable, int AddedOnDisc);

public sealed record TrafficCarRevertResult(int TotalTrafficCars, int NewlyHidden, int RemovedBuckets, int RestoredUnobtainable);

/// <summary>
/// Cross-game (FH2–FH6) helper that unlocks drivable traffic cars for Autoshow purchase
/// without deleting the free-roam TrafficCars spawn table or introducing schema/FK violations.
/// </summary>
public static class TrafficCarAutoshowService
{
    static readonly long[] NullExcludedIds = { 1215L, 1477L };

    public static IReadOnlyList<long> ResolveTrafficCarIds(
        SqliteConnection conn,
        Func<string, bool> tableExists,
        Func<string, string, bool> hasColumn,
        bool stockReferenceAttached)
    {
        var ids = new SortedSet<long>();
        if (tableExists("TrafficCars") && hasColumn("TrafficCars", "CarId"))
        {
            using var cmd = conn.CreateCommand();
            cmd.CommandText = "SELECT DISTINCT CarId FROM main.TrafficCars WHERE CarId IS NOT NULL";
            using var r = cmd.ExecuteReader();
            while (r.Read()) ids.Add(r.GetInt64(0));
        }
        if (stockReferenceAttached)
        {
            using var chk = conn.CreateCommand();
            chk.CommandText = "SELECT COUNT(*) FROM stockref.sqlite_master WHERE type='table' AND name='TrafficCars'";
            if (Convert.ToInt64(chk.ExecuteScalar()) > 0)
            {
                using var cmd = conn.CreateCommand();
                cmd.CommandText = "SELECT DISTINCT CarId FROM stockref.TrafficCars WHERE CarId IS NOT NULL";
                using var r = cmd.ExecuteReader();
                while (r.Read()) ids.Add(r.GetInt64(0));
            }
        }
        if (ids.Count == 0) return Array.Empty<long>();

        var nullFilterIds = new HashSet<long>(NullExcludedIds);
        if (hasColumn("CarListFilters", "CarID") && hasColumn("CarListFilters", "IsNullCar"))
        {
            using var cmd = conn.CreateCommand();
            cmd.CommandText = "SELECT DISTINCT CarID FROM main.CarListFilters WHERE IsNullCar=1 AND CarID IS NOT NULL";
            using var r = cmd.ExecuteReader();
            while (r.Read()) nullFilterIds.Add(r.GetInt64(0));
        }

        var existing = new HashSet<long>();
        using (var cmd = conn.CreateCommand())
        {
            cmd.CommandText = "SELECT Id, MediaName FROM main.Data_Car";
            using var r = cmd.ExecuteReader();
            while (r.Read())
            {
                long id = r.GetInt64(0);
                string media = r.IsDBNull(1) ? "" : r.GetString(1);
                if (media.Contains("NUL", StringComparison.OrdinalIgnoreCase) ||
                    media.Contains("TransitSSV", StringComparison.OrdinalIgnoreCase))
                {
                    nullFilterIds.Add(id);
                    continue;
                }
                existing.Add(id);
            }
        }

        return ids.Where(id => existing.Contains(id) && !nullFilterIds.Contains(id)).ToArray();
    }

    public static Dictionary<long, TrafficCarBaseline> CaptureBaselines(
        SqliteConnection conn,
        IReadOnlyCollection<long> trafficIds,
        Func<string, bool> tableExists,
        Func<string, string, bool> hasColumn,
        bool stockReferenceAttached)
    {
        var baselines = new Dictionary<long, TrafficCarBaseline>();
        if (trafficIds.Count == 0) return baselines;

        var stockCarIds = new HashSet<long>();
        if (stockReferenceAttached)
        {
            using var cmd = conn.CreateCommand();
            cmd.CommandText = "SELECT Id FROM stockref.Data_Car";
            using var r = cmd.ExecuteReader();
            while (r.Read()) stockCarIds.Add(r.GetInt64(0));
        }

        foreach (long carId in trafficIds)
        {
            string db = (stockReferenceAttached && stockCarIds.Contains(carId)) ? "stockref" : "main";
            baselines[carId] = ReadBaselineFromSchema(conn, db, carId, tableExists, hasColumn);
        }
        return baselines;
    }

    static TrafficCarBaseline ReadBaselineFromSchema(
        SqliteConnection conn,
        string db,
        long carId,
        Func<string, bool> tableExists,
        Func<string, string, bool> hasColumn)
    {
        bool SchemaHasTable(string table)
        {
            if (db == "main") return tableExists(table);
            using var chk = conn.CreateCommand();
            chk.CommandText = $"SELECT COUNT(*) FROM {db}.sqlite_master WHERE type='table' AND name=@t";
            chk.Parameters.AddWithValue("@t", table);
            return Convert.ToInt64(chk.ExecuteScalar()) > 0;
        }
        HashSet<string> SchemaColumns(string table)
        {
            if (db == "main" && !SchemaHasTable(table)) return new(StringComparer.OrdinalIgnoreCase);
            var s = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            using var p = conn.CreateCommand();
            p.CommandText = $"PRAGMA {db}.table_info(\"{table}\")";
            using var r = p.ExecuteReader();
            while (r.Read()) s.Add(r.GetString(1));
            return s;
        }

        var dcCols = SchemaColumns("Data_Car");
        string ColOrNull(string c) => dcCols.Contains(c) ? $"\"{c}\"" : $"NULL AS \"{c}\"";

        string media = "";
        long? cid = null, notAv = null, visOwn = null, noAh = null, drv = null, pur = null, ins = null, uni = null, arc = null;
        using (var cmd = conn.CreateCommand())
        {
            cmd.CommandText = $@"SELECT MediaName, {ColOrNull("ContentId")}, {ColOrNull("NotAvailableInAutoshow")},
                                        {ColOrNull("VisibleOnlyIfOwned")}, {ColOrNull("NotAvailableInAuctionHouse")},
                                        {ColOrNull("IsDrivable")}, {ColOrNull("IsPurchased")}, {ColOrNull("IsInstalled")},
                                        {ColOrNull("IsUnicorn")}, {ColOrNull("IsArcade")}
                                 FROM {db}.Data_Car WHERE Id=@id";
            cmd.Parameters.AddWithValue("@id", carId);
            using var r = cmd.ExecuteReader();
            if (r.Read())
            {
                media = r.IsDBNull(0) ? "" : r.GetString(0);
                cid = ReadNullableLong(r, 1);
                notAv = ReadNullableLong(r, 2);
                visOwn = ReadNullableLong(r, 3);
                noAh = ReadNullableLong(r, 4);
                drv = ReadNullableLong(r, 5);
                pur = ReadNullableLong(r, 6);
                ins = ReadNullableLong(r, 7);
                uni = ReadNullableLong(r, 8);
                arc = ReadNullableLong(r, 9);
            }
        }

        bool inUnobt = false;
        if (SchemaHasTable("UnobtainableCars"))
        {
            using var cmd = conn.CreateCommand();
            cmd.CommandText = $"SELECT COUNT(*) FROM {db}.UnobtainableCars WHERE Ordinal=@id";
            cmd.Parameters.AddWithValue("@id", carId);
            inUnobt = Convert.ToInt64(cmd.ExecuteScalar()) > 0;
        }

        long? bktId = null, bktHero = null;
        if (SchemaHasTable("Data_Car_Buckets"))
        {
            var bCols = SchemaColumns("Data_Car_Buckets");
            using var cmd = conn.CreateCommand();
            cmd.CommandText = bCols.Contains("BucketHero")
                ? $"SELECT CarBucket, BucketHero FROM {db}.Data_Car_Buckets WHERE CarId=@id"
                : $"SELECT CarBucket, NULL FROM {db}.Data_Car_Buckets WHERE CarId=@id";
            cmd.Parameters.AddWithValue("@id", carId);
            using var r = cmd.ExecuteReader();
            if (r.Read())
            {
                bktId = ReadNullableLong(r, 0);
                bktHero = ReadNullableLong(r, 1);
            }
        }

        bool hasOnDisc = false;
        if (SchemaHasTable("OnDiscContent"))
        {
            using var cmd = conn.CreateCommand();
            cmd.CommandText = $"SELECT COUNT(*) FROM {db}.OnDiscContent WHERE ContentType=1 AND ContentId=@id";
            cmd.Parameters.AddWithValue("@id", carId);
            hasOnDisc = Convert.ToInt64(cmd.ExecuteScalar()) > 0;
        }

        long? ieCar = null, ieAudio = null;
        if (SchemaHasTable("AllIECars"))
        {
            var ieCols = SchemaColumns("AllIECars");
            if (ieCols.Contains("IncludeCar") && ieCols.Contains("IncludeEngineUpgradeAudio"))
            {
                using var cmd = conn.CreateCommand();
                cmd.CommandText = $"SELECT IncludeCar, IncludeEngineUpgradeAudio FROM {db}.AllIECars WHERE Id=@id";
                cmd.Parameters.AddWithValue("@id", carId);
                using var r = cmd.ExecuteReader();
                if (r.Read())
                {
                    ieCar = ReadNullableLong(r, 0);
                    ieAudio = ReadNullableLong(r, 1);
                }
            }
        }

        bool wasUnlockedAtCapture = IsCarUnlockedForAutoshow(conn, carId, SchemaHasTable, (t, c) => SchemaColumns(t).Contains(c));
        return new TrafficCarBaseline(
            carId, media, db == "stockref", wasUnlockedAtCapture,
            inUnobt, bktId, bktHero, hasOnDisc, cid, notAv, visOwn, noAh, drv, pur, ins, uni, arc, ieCar, ieAudio);
    }

    public static bool IsCarUnlockedForAutoshow(
        SqliteConnection conn,
        long carId,
        Func<string, bool> tableExists,
        Func<string, string, bool> hasColumn)
    {
        string Col(string name, string fallback) => hasColumn("Data_Car", name) ? $"\"{name}\"" : fallback;
        using (var cmd = conn.CreateCommand())
        {
            cmd.CommandText = $@"SELECT {Col("NotAvailableInAutoshow", "0")},
                                        {Col("VisibleOnlyIfOwned", "0")},
                                        {Col("IsDrivable", "1")},
                                        {Col("IsPurchased", "1")},
                                        {Col("IsInstalled", "1")},
                                        {Col("IsUnicorn", "0")}
                                 FROM main.Data_Car WHERE Id=@id";
            cmd.Parameters.AddWithValue("@id", carId);
            using var r = cmd.ExecuteReader();
            if (!r.Read()) return false;
            if ((ReadNullableLong(r, 0) ?? 0) != 0) return false;
            if ((ReadNullableLong(r, 1) ?? 0) != 0) return false;
            if ((ReadNullableLong(r, 2) ?? 0) != 1) return false;
            if ((ReadNullableLong(r, 3) ?? 0) != 1) return false;
            if ((ReadNullableLong(r, 4) ?? 0) != 1) return false;
            if ((ReadNullableLong(r, 5) ?? 0) != 0) return false;
        }

        if (tableExists("Data_Car_Buckets") && hasColumn("Data_Car_Buckets", "CarId") && hasColumn("Data_Car_Buckets", "CarBucket"))
        {
            using var cmd = conn.CreateCommand();
            cmd.CommandText = "SELECT CarBucket FROM main.Data_Car_Buckets WHERE CarId=@id";
            cmd.Parameters.AddWithValue("@id", carId);
            object? val = cmd.ExecuteScalar();
            if (val == null || val == DBNull.Value || Convert.ToInt64(val) <= 0) return false;
        }

        if (tableExists("UnobtainableCars") && hasColumn("UnobtainableCars", "Ordinal"))
        {
            using var cmd = conn.CreateCommand();
            cmd.CommandText = "SELECT COUNT(*) FROM main.UnobtainableCars WHERE Ordinal=@id";
            cmd.Parameters.AddWithValue("@id", carId);
            if (Convert.ToInt64(cmd.ExecuteScalar()) > 0) return false;
        }

        return true;
    }

    public static TrafficCarUnlockResult Unlock(
        SqliteConnection conn,
        IReadOnlyList<long> targetCarIds,
        IReadOnlyList<CarBucketChoice> buckets,
        ForzaGame game,
        Func<string, bool> tableExists,
        Func<string, string, bool> hasColumn,
        IDictionary<long, long> sessionInsertedOnDiscRowIds)
    {
        if (targetCarIds.Count == 0)
            return new TrafficCarUnlockResult(0, 0, 0, 0, 0);

        var usageCounts = LoadBucketUsageCounts(conn, tableExists, hasColumn);
        var mediaById = LoadMediaNames(conn, targetCarIds);

        int newlyUnlocked = 0, addedBuckets = 0, clearedUnobtainable = 0, addedOnDisc = 0;

        ExecuteInTransaction(conn, () =>
        {
            bool hasUnobt = tableExists("UnobtainableCars") && hasColumn("UnobtainableCars", "Ordinal");
            bool hasBuckets = tableExists("Data_Car_Buckets") && hasColumn("Data_Car_Buckets", "CarId") && hasColumn("Data_Car_Buckets", "CarBucket");
            bool hasBucketHero = hasBuckets && hasColumn("Data_Car_Buckets", "BucketHero");
            bool hasOnDisc = tableExists("OnDiscContent") && hasColumn("OnDiscContent", "ContentId") && hasColumn("OnDiscContent", "ContentType");
            bool hasOnDiscReleaseOrder = hasOnDisc && hasColumn("OnDiscContent", "ReleaseOrder");
            bool hasOffersMap = tableExists("ContentOffersMapping") && hasColumn("ContentOffersMapping", "ContentId") && hasColumn("ContentOffersMapping", "ContentType");
            bool hasAllIe = tableExists("AllIECars") && hasColumn("AllIECars", "Id") && hasColumn("AllIECars", "IncludeCar") && hasColumn("AllIECars", "IncludeEngineUpgradeAudio");

            long nextOnDiscId = hasOnDisc
                ? Convert.ToInt64(Scalar(conn, "SELECT COALESCE(MAX(Id),0) FROM main.OnDiscContent") ?? 0L) + 1
                : 1;

            foreach (long carId in targetCarIds)
            {
                bool wasUnlocked = IsCarUnlockedForAutoshow(conn, carId, tableExists, hasColumn);
                string media = mediaById.GetValueOrDefault(carId, "");

                // 1. Update Data_Car visibility & drivability flags cleanly per game schema
                var setClauses = new List<string>();
                if (hasColumn("Data_Car", "IsDrivable")) setClauses.Add("IsDrivable=1");
                if (hasColumn("Data_Car", "IsPurchased")) setClauses.Add("IsPurchased=1");
                if (hasColumn("Data_Car", "IsInstalled")) setClauses.Add("IsInstalled=1");
                if (hasColumn("Data_Car", "IsUnicorn")) setClauses.Add("IsUnicorn=0");
                if (hasColumn("Data_Car", "IsArcade")) setClauses.Add("IsArcade=0");
                if (hasColumn("Data_Car", "NotAvailableInAutoshow")) setClauses.Add("NotAvailableInAutoshow=0");
                if (hasColumn("Data_Car", "VisibleOnlyIfOwned")) setClauses.Add("VisibleOnlyIfOwned=0");
                if (hasColumn("Data_Car", "NotAvailableInAuctionHouse")) setClauses.Add("NotAvailableInAuctionHouse=0");
                if (hasColumn("Data_Car", "BaseCost")) setClauses.Add("BaseCost=CASE WHEN BaseCost IS NULL OR BaseCost<0 THEN 0 ELSE BaseCost END");
                if (hasColumn("Data_Car", "ContentId"))
                {
                    if (game == ForzaGame.Horizon2)
                    {
                        setClauses.Add("ContentId=0");
                    }
                    else if (hasOffersMap)
                    {
                        setClauses.Add(@"ContentId=CASE WHEN ContentId IS NOT NULL AND ContentId<>0 AND ContentId<>99
                            AND NOT EXISTS (SELECT 1 FROM main.ContentOffersMapping m WHERE m.ContentType=1 AND (m.ContentId=CAST(Data_Car.Id AS TEXT) OR m.ContentId=Data_Car.Id))
                            THEN 0 ELSE ContentId END");
                    }
                }
                if (setClauses.Count > 0)
                    Exec(conn, $"UPDATE main.Data_Car SET {string.Join(",", setClauses)} WHERE Id=@id", carId);

                // 2. Remove from UnobtainableCars (FH4/5/6) — clean DELETE avoids FK errors & -1B orphans
                if (hasUnobt)
                {
                    int del = Exec(conn, "DELETE FROM main.UnobtainableCars WHERE Ordinal=@a OR Ordinal=@b", carId, -1000000000L - carId);
                    if (del > 0) clearedUnobtainable++;
                }

                // 3. Ensure valid assignable child CarBucket row in Data_Car_Buckets (FH2–FH6)
                if (hasBuckets)
                {
                    long? existingBucket = ScalarNullableLong(conn, "SELECT CarBucket FROM main.Data_Car_Buckets WHERE CarId=@id", carId, out bool rowExists);
                    if (!rowExists || existingBucket == null || existingBucket <= 0)
                    {
                        long? chosen = ChooseTrafficCarBucket(buckets, usageCounts, media);
                        if (chosen.HasValue)
                        {
                            if (!rowExists)
                            {
                                Exec(conn,
                                    hasBucketHero
                                        ? "INSERT INTO main.Data_Car_Buckets (CarId, CarBucket, BucketHero) VALUES (@id, @bkt, 0)"
                                        : "INSERT INTO main.Data_Car_Buckets (CarId, CarBucket) VALUES (@id, @bkt)",
                                    carId, chosen.Value);
                            }
                            else
                            {
                                Exec(conn,
                                    hasBucketHero
                                        ? "UPDATE main.Data_Car_Buckets SET CarBucket=@bkt, BucketHero=COALESCE(BucketHero,0) WHERE CarId=@id"
                                        : "UPDATE main.Data_Car_Buckets SET CarBucket=@bkt WHERE CarId=@id",
                                    chosen.Value, carId);
                            }
                            addedBuckets++;
                        }
                    }
                }

                // 4. Ensure OnDiscContent entry only if neither OnDiscContent nor ContentOffersMapping already grants it
                if (hasOnDisc)
                {
                    long inDisc = Convert.ToInt64(Scalar(conn, "SELECT COUNT(*) FROM main.OnDiscContent WHERE ContentType=1 AND ContentId=@id", carId) ?? 0L);
                    long inOffer = hasOffersMap
                        ? Convert.ToInt64(Scalar(conn, "SELECT COUNT(*) FROM main.ContentOffersMapping WHERE ContentType=1 AND (ContentId=CAST(@id AS TEXT) OR ContentId=@id)", carId) ?? 0L)
                        : 0L;
                    if (inDisc == 0 && inOffer == 0)
                    {
                        long rowId = nextOnDiscId++;
                        if (hasOnDiscReleaseOrder)
                            Exec(conn, "INSERT INTO main.OnDiscContent (Id, ContentId, ContentType, ReleaseOrder) VALUES (@rid, @cid, 1, 0)", rowId, carId);
                        else
                            Exec(conn, "INSERT INTO main.OnDiscContent (Id, ContentId, ContentType) VALUES (@rid, @cid, 1)", rowId, carId);
                        sessionInsertedOnDiscRowIds[carId] = rowId;
                        addedOnDisc++;
                    }
                }

                // 5. AllIECars flag if present (FH4–FH6)
                if (hasAllIe)
                    Exec(conn, "UPDATE main.AllIECars SET IncludeCar=1, IncludeEngineUpgradeAudio=1 WHERE Id=@id", carId);

                if (!wasUnlocked && IsCarUnlockedForAutoshow(conn, carId, tableExists, hasColumn))
                    newlyUnlocked++;
            }
        });

        return new TrafficCarUnlockResult(targetCarIds.Count, newlyUnlocked, addedBuckets, clearedUnobtainable, addedOnDisc);
    }

    public static TrafficCarRevertResult Revert(
        SqliteConnection conn,
        IReadOnlyList<long> targetCarIds,
        IReadOnlyDictionary<long, TrafficCarBaseline> baselines,
        Func<string, bool> tableExists,
        Func<string, string, bool> hasColumn,
        IDictionary<long, long> sessionInsertedOnDiscRowIds)
    {
        if (targetCarIds.Count == 0)
            return new TrafficCarRevertResult(0, 0, 0, 0);

        int newlyHidden = 0, removedBuckets = 0, restoredUnobtainable = 0;

        ExecuteInTransaction(conn, () =>
        {
            bool hasUnobt = tableExists("UnobtainableCars") && hasColumn("UnobtainableCars", "Ordinal");
            bool hasBuckets = tableExists("Data_Car_Buckets") && hasColumn("Data_Car_Buckets", "CarId") && hasColumn("Data_Car_Buckets", "CarBucket");
            bool hasBucketHero = hasBuckets && hasColumn("Data_Car_Buckets", "BucketHero");
            bool hasOnDisc = tableExists("OnDiscContent") && hasColumn("OnDiscContent", "Id");
            bool hasAllIe = tableExists("AllIECars") && hasColumn("AllIECars", "Id") && hasColumn("AllIECars", "IncludeCar") && hasColumn("AllIECars", "IncludeEngineUpgradeAudio");

            foreach (long carId in targetCarIds)
            {
                bool wasUnlocked = IsCarUnlockedForAutoshow(conn, carId, tableExists, hasColumn);
                baselines.TryGetValue(carId, out var rawBaseline);
                bool useBaseline = rawBaseline != null && (rawBaseline.IsFromStockRef || !rawBaseline.WasUnlockedAtCapture);
                var baseline = useBaseline ? rawBaseline : null;

                // 1. Restore Data_Car traffic-availability flags (never altering BaseCost or tuning)
                var setters = new List<(string Column, object? Value)>();
                void TryAdd(string col, long? baselineVal, long fallbackLockedVal)
                {
                    if (!hasColumn("Data_Car", col)) return;
                    setters.Add((col, baselineVal ?? fallbackLockedVal));
                }

                TryAdd("NotAvailableInAutoshow", baseline?.NotAvailableInAutoshow, 1L);
                TryAdd("VisibleOnlyIfOwned", baseline?.VisibleOnlyIfOwned, 0L);
                TryAdd("NotAvailableInAuctionHouse", baseline?.NotAvailableInAuctionHouse, 0L);
                TryAdd("IsDrivable", baseline?.IsDrivable, 1L);
                TryAdd("IsPurchased", baseline?.IsPurchased, 1L);
                TryAdd("IsInstalled", baseline?.IsInstalled, 1L);
                TryAdd("IsUnicorn", baseline?.IsUnicorn, 0L);
                TryAdd("IsArcade", baseline?.IsArcade, 0L);
                if (hasColumn("Data_Car", "ContentId") && baseline?.ContentId != null)
                    setters.Add(("ContentId", baseline.ContentId.Value));

                if (setters.Count > 0)
                {
                    using var cmd = conn.CreateCommand();
                    cmd.CommandText = $"UPDATE main.Data_Car SET {string.Join(",", setters.Select((s, i) => $"\"{s.Column}\"=@v{i}"))} WHERE Id=@id";
                    for (int i = 0; i < setters.Count; i++)
                        cmd.Parameters.AddWithValue($"@v{i}", setters[i].Value ?? (object)DBNull.Value);
                    cmd.Parameters.AddWithValue("@id", carId);
                    cmd.ExecuteNonQuery();
                }

                // 2. Restore Data_Car_Buckets row (keep baseline bucket if one existed in stock/baseline, else delete)
                if (hasBuckets)
                {
                    if (baseline?.BucketId is long origBucket && origBucket > 0)
                    {
                        long hero = baseline.BucketHero ?? 0L;
                        ScalarNullableLong(conn, "SELECT CarBucket FROM main.Data_Car_Buckets WHERE CarId=@id", carId, out bool exists);
                        if (exists)
                            Exec(conn,
                                hasBucketHero
                                    ? "UPDATE main.Data_Car_Buckets SET CarBucket=@b, BucketHero=@h WHERE CarId=@id"
                                    : "UPDATE main.Data_Car_Buckets SET CarBucket=@b WHERE CarId=@id",
                                hasBucketHero ? new object[] { origBucket, hero, carId } : new object[] { origBucket, carId });
                        else
                            Exec(conn,
                                hasBucketHero
                                    ? "INSERT INTO main.Data_Car_Buckets (CarId, CarBucket, BucketHero) VALUES (@id, @b, @h)"
                                    : "INSERT INTO main.Data_Car_Buckets (CarId, CarBucket) VALUES (@id, @b)",
                                hasBucketHero ? new object[] { carId, origBucket, hero } : new object[] { carId, origBucket });
                    }
                    else
                    {
                        int del = Exec(conn, "DELETE FROM main.Data_Car_Buckets WHERE CarId=@id", carId);
                        if (del > 0) removedBuckets++;
                    }
                }

                // 3. Restore UnobtainableCars (FH4–FH6)
                if (hasUnobt)
                {
                    Exec(conn, "DELETE FROM main.UnobtainableCars WHERE Ordinal=@neg", -1000000000L - carId);
                    bool shouldBeUnobt = baseline?.WasInUnobtainable ?? true;
                    if (shouldBeUnobt)
                    {
                        int ins = Exec(conn, "INSERT OR IGNORE INTO main.UnobtainableCars (Ordinal) VALUES (@id)", carId);
                        if (ins > 0) restoredUnobtainable++;
                    }
                    else
                    {
                        Exec(conn, "DELETE FROM main.UnobtainableCars WHERE Ordinal=@id", carId);
                    }
                }

                // 4. Delete only synthetic OnDiscContent rows inserted by this session
                if (hasOnDisc && sessionInsertedOnDiscRowIds.TryGetValue(carId, out long insertedId))
                {
                    Exec(conn, "DELETE FROM main.OnDiscContent WHERE Id=@rid AND ContentId=@cid AND ContentType=1", insertedId, carId);
                    sessionInsertedOnDiscRowIds.Remove(carId);
                }

                // 5. Restore AllIECars if baseline captured an entry
                if (hasAllIe && baseline?.AllIeIncludeCar != null && baseline.AllIeIncludeAudio != null)
                {
                    Exec(conn, "UPDATE main.AllIECars SET IncludeCar=@c, IncludeEngineUpgradeAudio=@a WHERE Id=@id",
                        baseline.AllIeIncludeCar.Value, baseline.AllIeIncludeAudio.Value, carId);
                }

                if (wasUnlocked && !IsCarUnlockedForAutoshow(conn, carId, tableExists, hasColumn))
                    newlyHidden++;
            }
        });

        return new TrafficCarRevertResult(targetCarIds.Count, newlyHidden, removedBuckets, restoredUnobtainable);
    }

    public static long? ChooseTrafficCarBucket(
        IReadOnlyList<CarBucketChoice> buckets,
        IReadOnlyDictionary<long, long> usageCounts,
        string mediaName)
    {
        var assignable = buckets.Where(b => b.CanAssign && b.Id > 0).ToList();
        if (assignable.Count == 0) return null;

        string u = (mediaName ?? "").ToUpperInvariant();
        long? PickByNames(params string[] preferredNames)
        {
            foreach (string name in preferredNames)
            {
                var exact = assignable.FirstOrDefault(b => string.Equals(b.Name, name, StringComparison.OrdinalIgnoreCase));
                if (exact != null) return exact.Id;
            }
            return null;
        }
        long? PickByFallbackIds(params long[] ids)
        {
            foreach (long id in ids)
                if (assignable.Any(b => b.Id == id)) return id;
            return null;
        }

        if (u.Contains("BUS") || u.Contains("TRUCK") || u.Contains("TRANSIT") || u.Contains("GRITTER") ||
            u.Contains("TRACTOR") || u.Contains("ACTY") || u.Contains("FX4") || u.Contains("TAXI") || u.Contains("VANDURA"))
        {
            long? hit = PickByNames("Vans and Utility", "Utility Heroes", "Trucks")
                ?? PickByFallbackIds(38, 16, 45);
            if (hit.HasValue) return hit.Value;
        }
        else if (u.Contains("X5M") || u.Contains("DEFENDER") || u.Contains("WRANGLER") || u.Contains("G65") ||
                 u.Contains("PAJERO") || u.Contains("COLORADO") || u.Contains("BRONCO") || u.Contains("RAPTOR") ||
                 u.Contains("F150") || u.Contains("XCLASS"))
        {
            long? hit = PickByNames("Sports Utility Heroes", "Pickups & 4x4's", "Offroad")
                ?? PickByFallbackIds(39, 48, 40, 29, 28);
            if (hit.HasValue) return hit.Value;
        }
        else if (u.Contains("BEETLE") || (u.Contains("COOPERS") && u.Contains("_65")))
        {
            long? hit = PickByNames("Cult Cars", "Rare Classics", "Retro Hot Hatch")
                ?? PickByFallbackIds(21, 22, 24);
            if (hit.HasValue) return hit.Value;
        }
        else if (u.Contains("FIESTA") || u.Contains("500ABARTH") || u.Contains("PUNTO") || u.Contains("_KA_") ||
                 u.Contains("_FOX_") || u.Contains("CEED") || u.Contains("YARIS") || u.Contains("JOHNCOOPER") ||
                 u.Contains("MAZDASPEED3") || u.Contains("HON_E_"))
        {
            long? hit = PickByNames("Hot Hatch", "Super Hot Hatch", "Retro Hot Hatch")
                ?? PickByFallbackIds(23, 25, 24);
            if (hit.HasValue) return hit.Value;
        }
        else if (u.Contains("BORA") || u.Contains("C32") || u.Contains("_S4_") || u.Contains("_155_") ||
                 u.Contains("323SPORT") || u.Contains("GALANT") || u.Contains("A200") || u.Contains("LEGACY") ||
                 u.Contains("GTIVR6") || u.Contains("BRERA") || u.Contains("_WRX_") || u.Contains("STAGEA"))
        {
            long? hit = PickByNames("Retro Saloons", "Retro Super Saloons", "Super Saloons", "Modern Super Saloons",
                                    "Saloon Cars", "Retro Rally", "Modern Rally", "Modern Sports Cars", "Retro Sports Cars")
                ?? PickByFallbackIds(14, 19, 35, 32, 18, 17);
            if (hit.HasValue) return hit.Value;
        }

        return assignable
            .OrderByDescending(b => usageCounts.GetValueOrDefault(b.Id, 0L))
            .ThenBy(b => b.Id)
            .First().Id;
    }

    static Dictionary<long, long> LoadBucketUsageCounts(SqliteConnection conn, Func<string, bool> tableExists, Func<string, string, bool> hasColumn)
    {
        var counts = new Dictionary<long, long>();
        if (!tableExists("Data_Car_Buckets") || !hasColumn("Data_Car_Buckets", "CarBucket")) return counts;
        using var cmd = conn.CreateCommand();
        cmd.CommandText = "SELECT CarBucket, COUNT(*) FROM main.Data_Car_Buckets WHERE CarBucket IS NOT NULL AND CarBucket>0 GROUP BY CarBucket";
        using var r = cmd.ExecuteReader();
        while (r.Read()) counts[r.GetInt64(0)] = r.GetInt64(1);
        return counts;
    }

    static Dictionary<long, string> LoadMediaNames(SqliteConnection conn, IReadOnlyCollection<long> carIds)
    {
        var map = new Dictionary<long, string>();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = $"SELECT Id, MediaName FROM main.Data_Car WHERE Id IN ({string.Join(",", carIds)})";
        using var r = cmd.ExecuteReader();
        while (r.Read()) map[r.GetInt64(0)] = r.IsDBNull(1) ? "" : r.GetString(1);
        return map;
    }

    static void ExecuteInTransaction(SqliteConnection conn, Action work)
    {
        Exec(conn, "BEGIN IMMEDIATE");
        try
        {
            work();
            Exec(conn, "COMMIT");
        }
        catch
        {
            try { Exec(conn, "ROLLBACK"); } catch { }
            throw;
        }
    }

    static long? ReadNullableLong(SqliteDataReader r, int ordinal) =>
        r.IsDBNull(ordinal) ? null : Convert.ToInt64(r.GetValue(ordinal));

    static object? Scalar(SqliteConnection conn, string sql, params object[] args)
    {
        using var cmd = PrepareCommand(conn, sql, args);
        object? val = cmd.ExecuteScalar();
        return val == DBNull.Value ? null : val;
    }

    static long? ScalarNullableLong(SqliteConnection conn, string sql, long id, out bool rowExists)
    {
        using var cmd = conn.CreateCommand();
        cmd.CommandText = sql;
        cmd.Parameters.AddWithValue("@id", id);
        using var r = cmd.ExecuteReader();
        if (!r.Read()) { rowExists = false; return null; }
        rowExists = true;
        return ReadNullableLong(r, 0);
    }

    static int Exec(SqliteConnection conn, string sql, params object[] args)
    {
        using var cmd = PrepareCommand(conn, sql, args);
        return cmd.ExecuteNonQuery();
    }

    static SqliteCommand PrepareCommand(SqliteConnection conn, string sql, object[] args)
    {
        var cmd = conn.CreateCommand();
        if (System.Text.RegularExpressions.Regex.IsMatch(sql, @"@[a-zA-Z_]"))
        {
            var seen = new List<string>();
            foreach (System.Text.RegularExpressions.Match m in System.Text.RegularExpressions.Regex.Matches(sql, @"@[a-zA-Z_][a-zA-Z0-9_]*"))
                if (!seen.Contains(m.Value)) seen.Add(m.Value);
            for (int i = 0; i < seen.Count && i < args.Length; i++)
                cmd.Parameters.AddWithValue(seen[i], args[i] ?? DBNull.Value);
            cmd.CommandText = sql;
        }
        else
        {
            int i = 0;
            foreach (var arg in args) cmd.Parameters.AddWithValue($"@p{i++}", arg ?? DBNull.Value);
            int p = 0;
            cmd.CommandText = System.Text.RegularExpressions.Regex.Replace(sql, @"\?", _ => $"@p{p++}");
        }
        return cmd;
    }
}
