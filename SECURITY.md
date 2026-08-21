# セキュリティポリシー

## 脆弱性の報告

セキュリティ関連の問題は **公開 Issue には投稿せず**、以下のいずれかで連絡してください。

- GitHub の [Private Security Advisories](https://github.com/kenny7968/kxEdit/security/advisories/new)
- メール: y27553@gmail.com (件名に `[kxEdit security]` を含めてください)

Private Security Advisories が利用できない場合は、公開 Issue に「セキュリティ / 詳細は非公開で」とだけ書いていただければ、折り返し非公開の連絡手段をお知らせします。

## 対応方針

- 個人プロジェクトのため対応日数の保証はできません。可能な限り速やかに一次応答します
- 修正版は GitHub Releases (`v*` タグ) として配布します
- 深刻度が高い問題については、修正リリースと同時に GitHub Security Advisory を公開します

## サポート対象バージョン

最新の GitHub Releases のみサポートします。旧バージョンへの遡及修正は行いません。

## 想定される攻撃面

参考までに、kxEdit で特に注意している攻撃面を挙げます (これ以外の報告も歓迎します)。

- **Markdown プレビュー**: WebView2 上で Markdig によりレンダリング。任意 HTML / JavaScript の実行、外部リソースの取得、CSP 迂回など
- **CSV / テキストファイルの読み込み**: 巨大ファイル・不正エンコーディング・特殊な区切りによる DoS、パーサ例外
- **バックアップファイル**: 復元機能を悪用したパストラバーサル、任意ファイル上書き
- **UI Automation プロバイダ**: スクリーンリーダー向け UIA 経路で機微情報を過剰に露出していないか

## 対象外

- 依存ライブラリ (Markdig, WebView2, UTF.Unknown 等) 自体の脆弱性は、まず各上流に報告してください
- ソーシャルエンジニアリング、物理アクセスを前提とした攻撃
