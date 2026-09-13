namespace BusinessApp.Schema.Tests;

/// <summary>
/// <b>クエリの SQL を読むテストは、並べて走らせない。</b>
/// </summary>
/// <remarks>
/// <para><b>SQL ミューテーションの注入はプロセス共通の環境変数である</b>
/// （<c>TestDatabase.SqlMutationVariable</c>。ADR-0056 決定 3）。
/// xunit は<b>クラス単位で並列に走らせるのが既定</b>なので、
/// <see cref="SqlMutationTests"/> が変数を立てている窓の中で
/// 行動テストが同じ SQL を読むと、<b>壊れた SQL で走って不定に赤くなる</b>。</para>
/// <para><b>環境変数をやめて引数で渡す形にはできない。</b>
/// 掃引は<b>別プロセス</b>のテストランナーに注入するので、
/// プロセス共通の口が要る（ファイルを書き換えないための選択でもある。同 決定 3）。</para>
/// <para><b>直列になるのはこの 5 クラスだけ</b>で、ほかは並列のままである。</para>
/// </remarks>
[CollectionDefinition(Name)]
public sealed class QuerySqlCollection
{
    public const string Name = "クエリの SQL を読むテスト";
}
