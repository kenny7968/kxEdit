using System.Reflection;

namespace kxEdit.App.Tests;

/// <summary>
/// 単一インスタンス機構の時間予算が<b>厳密に入れ子</b>であることを固定する
/// (設計 2026-09-14 §4 / <see cref="SingleInstanceServer"/> の remarks が唯一の正)。
/// <code>
///   ActivateTimeout(3s) &lt; AckTimeout(4s) &lt; PerConnectionTimeout(5s) &lt; Dispose の待ち(7s)
/// </code>
/// <para>
/// <b>なぜ網が要るか</b>: 3 か所の xmldoc が「等号にしてはならない」と繰り返しているのに、
/// 等号にする変異はどのテストでも赤くならなかった(レビュー指摘 I-4)。値は
/// <see cref="PendingActivation"/> / <see cref="Program"/> / <see cref="SingleInstanceServer"/> の
/// 3 クラスに分かれた private / internal な定数で、ドリフトしても単一ビルド内のテストは全緑になる。
/// 症状は「<b>前面化は成功しているのにエラーダイアログも出る</b>」——しかも確率的にしか出ない。
/// </para>
/// <para>
/// <b>逆転だけでなく等号も赤にする</b>のが要点。<c>ActivateTimeout == AckTimeout</c> だと、
/// 前面化が期限いっぱいかかったときに 2 つ目が待ちきれず <c>NoResponse</c> に落ちる窓が開く。
/// </para>
/// <para>
/// <b>フィールド名への結合は承知の上</b>。名前を変えれば <c>Assert.NotNull</c> で赤くなるが、
/// それは望ましい方向である —— 予算の担い手が改名・移動したなら、入れ子の不変条件が
/// まだ成り立っているかを人間が見直すべき瞬間だからである。<b>見つからないことを
/// 「検証不要」に読み替えて削らないこと。</b>
/// </para>
/// <para>
/// <b>守らないもの</b>: 実際の待ち時間(実測は各層のテストと L5 の担当)と、
/// <c>Dispose</c> の待ち(<see cref="SingleInstanceServer"/> が
/// <c>PerConnectionTimeout + 2 秒</c>として自動的に満たす)。
/// </para>
/// </summary>
public class SingleInstanceTimeoutBudgetTests
{
    private static TimeSpan StaticTimeSpan(Type type, string fieldName)
    {
        var field = type.GetField(
            fieldName,
            BindingFlags.Static | BindingFlags.NonPublic | BindingFlags.Public
        );
        Assert.True(
            field is not null,
            $"{type.Name}.{fieldName} が見つからない。期限の担い手が改名・移動したなら、"
                + "入れ子の不変条件(SingleInstanceServer の remarks)がまだ成り立つかを見直すこと"
        );
        Assert.Equal(typeof(TimeSpan), field!.FieldType);
        return (TimeSpan)field.GetValue(null)!;
    }

    [Fact]
    public void Timeout_budget_is_strictly_nested()
    {
        var activate = StaticTimeSpan(typeof(PendingActivation), "ActivateTimeout");
        var ack = StaticTimeSpan(typeof(Program), "AckTimeout");
        var connect = StaticTimeSpan(typeof(Program), "ConnectTimeout");
        var perConnection = SingleInstanceServer.DefaultPerConnectionTimeout;

        Assert.True(
            activate < ack,
            "前面化待ちは ACK 待ちより【厳密に】短いこと。等号でも「前面化は成功しているのに"
                + $"2 つ目がエラーダイアログを出す」窓が開く。実際: activate={activate}, ack={ack}"
        );
        Assert.True(
            ack < perConnection,
            "ACK 待ちは 1 接続あたりの上限より【厳密に】短いこと。逆だとサーバが先に接続を畳んで"
                + $"正常な ACK を取り逃す。実際: ack={ack}, perConnection={perConnection}"
        );
        // 接続確立は別予算だが、1 接続の上限を超えて待つ意味は無い(相手はとっくに畳んでいる)。
        Assert.True(
            connect <= perConnection,
            $"接続確立の上限が 1 接続あたりの上限を超えている。実際: connect={connect}, perConnection={perConnection}"
        );
    }
}
