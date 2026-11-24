using System.Data;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Storage;
using Paysys.DAL.Persistence;

namespace Paysys.BLL.Services;

// One SQL statement with named parameters, for the few places where a single atomic statement is the point
// (take a token, count a failure). Joins the context's open transaction when there is one.
internal static class RawSql
{
    public static object? Scalar(PaysysDbContext db, string sql, params (string Name, object? Value)[] args) =>
        Run(db, sql, args, command => command.ExecuteScalar());

    public static void Execute(PaysysDbContext db, string sql, params (string Name, object? Value)[] args) =>
        Run(db, sql, args, command => command.ExecuteNonQuery());

    private static object? Run(PaysysDbContext db, string sql, (string Name, object? Value)[] args, Func<System.Data.Common.DbCommand, object?> execute)
    {
        var connection = db.Database.GetDbConnection();
        var wasOpen = connection.State == ConnectionState.Open;
        if (!wasOpen)
            db.Database.OpenConnection();

        try
        {
            using var command = connection.CreateCommand();
            command.CommandText = sql;
            command.Transaction = db.Database.CurrentTransaction?.GetDbTransaction();
            foreach (var (name, value) in args)
            {
                var parameter = command.CreateParameter();
                parameter.ParameterName = name;
                parameter.Value = value ?? DBNull.Value;
                command.Parameters.Add(parameter);
            }

            return execute(command);
        }
        finally
        {
            if (!wasOpen)
                db.Database.CloseConnection();
        }
    }
}
