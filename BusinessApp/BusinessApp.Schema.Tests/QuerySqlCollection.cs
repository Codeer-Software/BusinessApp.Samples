namespace BusinessApp.Schema.Tests;

/// <summary>
/// <b>クエリの SQL を読むテストは、並べて走らせない。</b>
/// </summary>
/// <remarks>
/// <para><b>SQL ミューテーションの注入はプロセス共通の環境変数である</b>
/// （A 案は <c>TestDatabase.SqlMutationVariable</c>。ADR-0056 決定 3。
/// B 案は <c>SqlMutationProbe.SpecVariable</c> と <c>ReportVariable</c>。ADR-0058 決定 2）。
/// xunit は<b>クラス単位で並列に走らせるのが既定</b>なので、
/// <see cref="SqlMutationTests"/> や <see cref="SqlMutationProbeTests"/> が変数を立てている窓の中で
/// 行動テストが同じ SQL を読むと、<b>壊れた SQL で走って不定に赤くなる</b>——
/// 逆に、<b>行動テストが流したクエリを計器が観測してしまう</b>形も同じ窓で起きる。</para>
/// <para><b>環境変数をやめて引数で渡す形にはできない。</b>
/// 掃引は<b>別プロセス</b>のテストランナーに注入するので、
/// プロセス共通の口が要る（ファイルを書き換えないための選択でもある。同 決定 3）。</para>
/// <para><b>直列になるのは、クエリの SQL を読むクラスと 2 つの計器のクラスだけ</b>で、
/// ほかは並列のままである。<b>数を書かない</b>——足した日に書き換える人がいないと必ず腐る。</para>
/// </remarks>
[CollectionDefinition(Name)]
public sealed class QuerySqlCollection
{
    public const string Name = "クエリの SQL を読むテスト";
}
