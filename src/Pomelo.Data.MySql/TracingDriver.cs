﻿// Copyright (c) Pomelo Foundation. All rights reserved.
// Licensed under the MIT. See LICENSE in the project root for license information.

using System;
using System.Text;
using Pomelo.Data.Types;
using System.Diagnostics;
using System.Collections.Generic;

using System.Threading;
using Pomelo.Data.Common;

namespace Pomelo.Data.MySql
{
  internal class TracingDriver : Driver
  {
    private static long driverCounter;
    private long driverId;
    private ResultSet activeResult;
    private int rowSizeInBytes;

    public TracingDriver(MySqlConnectionStringBuilder settings)
      : base(settings)
    {
      driverId = Interlocked.Increment(ref driverCounter);
    }

    public override void Open()
    {
      base.Open();
      MySqlTrace.TraceEvent(TraceEventType.Information, MySqlTraceEventType.ConnectionOpened,
          Resources.TraceOpenConnection, driverId, Settings.ConnectionString, ThreadID);
    }

    public override void Close()
    {
      base.Close();
      MySqlTrace.TraceEvent(TraceEventType.Information, MySqlTraceEventType.ConnectionClosed,
          Resources.TraceCloseConnection, driverId);
    }

    public override void SendQuery(MySqlPacket p)
    {
      rowSizeInBytes = 0;
      string cmdText = Encoding.GetString(p.Buffer, 5, p.Length - 5);
      string normalized_query = null;

      if (cmdText.Length > 300)
      {
        QueryNormalizer normalizer = new QueryNormalizer();
        normalized_query = normalizer.Normalize(cmdText);
        cmdText = cmdText.Substring(0, 300);
      }

      base.SendQuery(p);

      MySqlTrace.TraceEvent(TraceEventType.Information, MySqlTraceEventType.QueryOpened,
          Resources.TraceQueryOpened, driverId, ThreadID, cmdText);
      if (normalized_query != null)
        MySqlTrace.TraceEvent(TraceEventType.Information, MySqlTraceEventType.QueryNormalized,
            Resources.TraceQueryNormalized, driverId, ThreadID, normalized_query);
    }

    protected override long GetResult(int statementId, ref long affectedRows, ref long insertedId)
    {
      try
      {
        var fieldCount = base.GetResult(statementId, ref affectedRows, ref insertedId);
        MySqlTrace.TraceEvent(TraceEventType.Information, MySqlTraceEventType.ResultOpened,
            Resources.TraceResult, driverId, fieldCount, affectedRows, insertedId);

        return fieldCount;
      }
      catch (MySqlException ex)
      {
        // we got an error so we report it
        MySqlTrace.TraceEvent(TraceEventType.Information, MySqlTraceEventType.Error,
            Resources.TraceOpenResultError, driverId, ex.Number, ex.Message);
        throw ex;
      }
    }

    public override ResultSet NextResult(int statementId, bool force)
    {
      // first let's see if we already have a resultset on this statementId
      if (activeResult != null)
      {
        //oldRS = activeResults[statementId];
        if (Settings.UseUsageAdvisor)
          ReportUsageAdvisorWarnings(statementId, activeResult);
        MySqlTrace.TraceEvent(TraceEventType.Information, MySqlTraceEventType.ResultClosed,
            Resources.TraceResultClosed, driverId, activeResult.TotalRows, activeResult.SkippedRows,
            rowSizeInBytes);
        rowSizeInBytes = 0;
        activeResult = null;
      }

      activeResult = base.NextResult(statementId, force);
      return activeResult;
    }

    public override int PrepareStatement(string sql, ref MySqlField[] parameters)
    {
      int statementId = base.PrepareStatement(sql, ref parameters);
      MySqlTrace.TraceEvent(TraceEventType.Information, MySqlTraceEventType.StatementPrepared,
          Resources.TraceStatementPrepared, driverId, sql, statementId);
      return statementId;
    }

    public override void CloseStatement(int id)
    {
      base.CloseStatement(id);
      MySqlTrace.TraceEvent(TraceEventType.Information, MySqlTraceEventType.StatementClosed,
          Resources.TraceStatementClosed, driverId, id);
    }

    public override void SetDatabase(string dbName)
    {
      base.SetDatabase(dbName);
      MySqlTrace.TraceEvent(TraceEventType.Information, MySqlTraceEventType.NonQuery,
          Resources.TraceSetDatabase, driverId, dbName);
    }

    public override void ExecuteStatement(MySqlPacket packetToExecute)
    {
      base.ExecuteStatement(packetToExecute);
      int pos = packetToExecute.Position;
      packetToExecute.Position = 1;
      int statementId = packetToExecute.ReadInteger(4);
      packetToExecute.Position = pos;

      MySqlTrace.TraceEvent(TraceEventType.Information, MySqlTraceEventType.StatementExecuted,
          Resources.TraceStatementExecuted, driverId, statementId, ThreadID);
    }

    public override bool FetchDataRow(int statementId, int columns)
    {
      try
      {
        bool b = base.FetchDataRow(statementId, columns);
        if (b)
          rowSizeInBytes += (handler as NativeDriver).Packet.Length;
        return b;
      }
      catch (MySqlException ex)
      {
        MySqlTrace.TraceEvent(TraceEventType.Error, MySqlTraceEventType.Error,
            Resources.TraceFetchError, driverId, ex.Number, ex.Message);
        throw ex;
      }
    }

    public override void CloseQuery(MySqlConnection connection, int statementId)
    {
      base.CloseQuery(connection, statementId);

      MySqlTrace.TraceEvent(TraceEventType.Information, MySqlTraceEventType.QueryClosed,
          Resources.TraceQueryDone, driverId);
    }

    public override List<MySqlError> ReportWarnings(MySqlConnection connection)
    {
      List<MySqlError> warnings = base.ReportWarnings(connection);
      foreach (MySqlError warning in warnings)
        MySqlTrace.TraceEvent(TraceEventType.Warning, MySqlTraceEventType.Warning,
            Resources.TraceWarning, driverId, warning.Level, warning.Code, warning.Message);
      return warnings;
    }

    private bool AllFieldsAccessed(ResultSet rs)
    {
      if (rs.Fields == null || rs.Fields.Length == 0) return true;

      for (int i = 0; i < rs.Fields.Length; i++)
        if (!rs.FieldRead(i)) return false;
      return true;
    }

    private void ReportUsageAdvisorWarnings(int statementId, ResultSet rs)
    {
#if !RT
      if (!Settings.UseUsageAdvisor) return;

      if (HasStatus(ServerStatusFlags.NoIndex))
        MySqlTrace.TraceEvent(TraceEventType.Warning, MySqlTraceEventType.UsageAdvisorWarning,
            Resources.TraceUAWarningNoIndex, driverId, UsageAdvisorWarningFlags.NoIndex);
      else if (HasStatus(ServerStatusFlags.BadIndex))
        MySqlTrace.TraceEvent(TraceEventType.Warning, MySqlTraceEventType.UsageAdvisorWarning,
            Resources.TraceUAWarningBadIndex, driverId, UsageAdvisorWarningFlags.BadIndex);

      // report abandoned rows
      if (rs.SkippedRows > 0)
        MySqlTrace.TraceEvent(TraceEventType.Warning, MySqlTraceEventType.UsageAdvisorWarning,
            Resources.TraceUAWarningSkippedRows, driverId, UsageAdvisorWarningFlags.SkippedRows, rs.SkippedRows);

      // report not all fields accessed
      if (!AllFieldsAccessed(rs))
      {
        StringBuilder notAccessed = new StringBuilder("");
        string delimiter = "";
        for (int i = 0; i < rs.Size; i++)
          if (!rs.FieldRead(i))
          {
            notAccessed.AppendFormat("{0}{1}", delimiter, rs.Fields[i].ColumnName);
            delimiter = ",";
          }
        MySqlTrace.TraceEvent(TraceEventType.Warning, MySqlTraceEventType.UsageAdvisorWarning,
            Resources.TraceUAWarningSkippedColumns, driverId, UsageAdvisorWarningFlags.SkippedColumns,
                notAccessed.ToString());
      }

      // report type conversions if any
      if (rs.Fields != null)
      {
        foreach (MySqlField f in rs.Fields)
        {
          StringBuilder s = new StringBuilder();
          string delimiter = "";
          foreach (Type t in f.TypeConversions)
          {
            s.AppendFormat("{0}{1}", delimiter, t.Name);
            delimiter = ",";
          }
          if (s.Length > 0)
            MySqlTrace.TraceEvent(TraceEventType.Warning, MySqlTraceEventType.UsageAdvisorWarning,
                Resources.TraceUAWarningFieldConversion, driverId, UsageAdvisorWarningFlags.FieldConversion,
                f.ColumnName, s.ToString());
        }
      }
#endif
    }
  }
}
