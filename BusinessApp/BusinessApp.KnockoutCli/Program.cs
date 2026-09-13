// 制約ノックアウトの掃引の材料を書き出す（ADR-0053）。
//
//   （引数なし） 外す点の一覧。1 行 1 点で「名前<TAB>種類<TAB>どこ<TAB>流せるか」
//   --filter     殺し手から外すテストを除く dotnet test の filter 式
//   --excluded   除くテストとその理由（人が読むため）
//
// 「流せるか」を一覧に入れてあるのは、**「DDL が流せなくなった赤」を
// 「テストが見張っている赤」と混ぜないため**である（ADR-0053 決定 5）。
// 点ごとにプロセスを起こして聞くと 152 回分の起動代を払うので、ここで 1 度に数える。
//
// 掃引そのものは tools/clb/knockout.ps1 が回す。ここは印字しかしない。
using System.Text;

using BusinessApp.TestSupport;

using Microsoft.Data.Sqlite;

Console.OutputEncoding = Encoding.UTF8;

switch (args.Length == 0 ? string.Empty : args[0])
{
    case "":
        foreach (var point in SchemaKnockout.All())
        {
            Console.WriteLine($"{point.Name}\t{point.Kind}\t{point.Where}\t{(Applies(point.Name) ? "yes" : "no")}");
        }

        return 0;

    case "--filter":
        Console.WriteLine(SchemaKnockout.TestFilter());
        return 0;

    case "--excluded":
        foreach (var (name, reason) in SchemaKnockout.TestsThatReadSchemaText)
        {
            Console.WriteLine($"{name}\t{reason}");
        }

        return 0;

    default:
        Console.Error.WriteLine($"知らない引数である: {args[0]}（使えるのは --filter と --excluded だけ）");
        return 2;
}

static bool Applies(string name)
{
    try
    {
        using var db = SchemaKnockout.CreateWithout(name);
        return true;
    }
    catch (SqliteException)
    {
        return false;
    }
}
