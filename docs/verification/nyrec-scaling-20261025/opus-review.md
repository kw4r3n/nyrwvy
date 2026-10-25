Model: claude-opus-5-5. Independent review of the fixed authorized source and raw-sample payload; tools disabled. Findings are audited in the main report.

# `.nyrec` 性能調査レビュー(Opus、提供ペイロードのみ)

## 判定

**条件付きで受理し、修正を要します。**

- 見出しの数値、割当表の算術、非重複性、ディスクI/Oが小さいという結論は、生サンプルからすべて再現できました。
- 一方で、次の3点が弱いです。
  - 最適化順序の根拠
  - simulate が帰属分析されていないこと
  - GCモードの外的妥当性

## 生データで確立できた点

**見出し値**
- control の 1000 commit(891.5 / 923.0 / 1176.4 ms)の中央値は 923 ms です。
- 3000 commit の中央値は 2777 ms、割当中央値は 3,513,118,968 B(3350.4 MiB)です。
- GC pause の中央値は 171.2 ms、CPU の中央値は 5406.6 ms で、いずれも本文と一致します。

**commit 表(profiled 3000)**
- 各行の値は次のとおり一致します。
  - validation 1,872,724,128 B = 1786.0 MiB
  - json.write 649.9 MiB
  - record.encode 376.1 MiB
  - proposal-order 169.6 MiB
  - source-check 109.7 MiB
  - utf8 27.4 MiB
- 合計 3118.7 MiB に Other 237.6 MiB を足すと 3356.3 MiB になります。
- 各行の計測点はソース上で互いにネストしていません(serialize の後に ofJson、validate の後に newEffectOrderConflicts)。

**正規化モデルが2回目の検証であること**
- `serialization`(2回計算、1367.1 MiB)は、canonical 書き出し約2回分(649.9 − currentSerialization 210.0 ≈ 440)と検証1回分(≈891)の和にほぼ一致します。残差は約36 MiB です。

**decode 表**
- 529.6 + 894.7 = 1424.3 MiB で、`record.json-reader-inclusive` と一致します。
- 1593.1 − 1424.3 = 168.8 MiB で、record 固有4フェーズの合計と一致します。
- open の形式差(nyrec − json = 183.9 MiB、約416 ms)も record 固有コスト(168.8 MiB、約0.4 s)とおおむね整合しています。

**deep 表**
- 84.2 / 61.9 / 58.0 / 20.8 / 19.8 MiB と、resolver 19.9 / 46.0 MiB はすべて一致します。

**その他**
- process 割当と thread 割当の差は最大約7.7 KB で、同期的な帰属は妥当です。
- write+flush 19.5 ms、replace 31.5 ms で、ディスクは時間全体の約1%です。
- JSON 形式の commit は 2955.5 MiB で、差の約395 MiB も一致します。

## 指摘(重大度順)

### H1: simulate が未帰属で、commit が「再simulate+永続化」であることが明示されていない

- control 3000 の simulate 割当の中央値は 3,385,736,760 B(3228.9 MiB)で、commit の96%に当たります。nyrec と json で完全に同一です。
- commit 内訳から永続化固有の分を推定すると、currentSerialization 210.0 + record.encode 376.1 + source-check 109.7 + utf8 27.4 = 723.2 MiB(21.5%)です。
- 残りの約78%は、Workspace.fs:284 の再simulate系の処理です。
- 本文 55–56行の「同一オブジェクトの2回のキャッシュミスではない」は、参照同一性としては正しいです。しかし、編集1サイクルあたり、等価な値に対する検証・指紋計算が simulate と commit で2回行われるという事実が抜けています。
- 検証(#1)と正規化指紋(#4)の改善は、simulate と commit の両方に効きます。record 往復(#2の後半)は commit にしか効きません。
- 対応: simulate の内訳を既存の profiled.jsonl から出してください(新規実験は不要です)。

### H2: 最適化順序の根拠が不十分

「最大カテゴリ」と「最大の単一施策」が混同されています。

**#1 の確立済み下位コスト(検証289.6 MiB に対する比率)**
- resolver index: 6.9%
- 推論: 15.9%
- fn-rules: 21.4%
- setup-indexes: 20.0%
- どの単一変更も、検証の約2割(commit の約1割)を超えると示されていません。

**#4(正規化モデル検証)**
- 単一ブロックとしては最大です。
- 割当は約891 MiB(commit の26.6%)、時間は約0.85 s(2.89 s の約29%)です。ただし時間には GC pause の帰属バイアスがあります。
- simulate にも同じ処理が入っているはずです。
- 正しさの条件は「`validate s = []` ⇒ `validate (normalize s) = []`」という補題にほぼ帰着します。成立すれば出力バイトは不変で、変わるのは失敗時の挙動だけです。コーパスとプロパティテストで検証可能なので、最後に回す根拠は弱いです。

**#2(writeSnapshotAt、SnapshotCodec.fs:1333–1338)**
- MemoryStream の倍々成長、`ToArray`、`GetString`(UTF-16、2倍)が重なっています。JSON サイズを J とすると、およそ 2·pow2(J) + J + 2J です。
- 1回あたりの書き出し約216 MiB の大半がコピーである可能性があります。
- バイト同一を保てる機械的修正なので、最初に試すべきです。
- 欠落している証拠: fixture 行の `jsonBytes` 値です。

### M1: GCモードの外的妥当性

- fsi は Server GC で動いていました(environment.json:19)。
- CPU − elapsed ≈ 2.57 s(3000 commit s2)は、24スレッドの並列GCとして説明できる規模です。
- 実際のセッションホストの runtimeconfig が workstation GC であれば、pause、世代別回数、elapsed が移植できません。割当バイトは影響を受けません。
- 欠落している証拠: ホストの GC 設定です。異なる場合は control を `DOTNET_gcServer=0` で再実行してください。

### M2: 検証のグローバル setup が時間面で超線形になっている兆候

- deep 1000(validate-cold)の setup 系合計は約65 ms です。
- profiled 3000 の `validation.setup` は 289 / 369 / 343 ms(decode)、検証1回あたり 349 / 309 / 304 ms(commit)で、約5倍です。
- 一方、割当は 3.28倍、`validation.functions` の時間は約3.1倍です。
- `Map.ofList` による O(n log n) の挿入コピー(Validation.fs:229–232 など)や、長寿命の木構造の昇格が疑われます。
- ただし deep と profiled は別ビルドです。profiled の 1000 と 3000 で `validation.setup` の ticks を比較して確認してください(既存データで可能)。
- 確認できれば、#1 の内部では global index を先に扱うのが妥当です。
- 最小案: nodeOwners を Dictionary に置き換え、indexer 代入で last-wins を維持します。診断は 2751–2753行でソートされるため、順序への影響は小さいです。

### M3: 「Other」に指紋ハッシュ経路が含まれている(推定)

- `snapshotFingerprint` 285.1 MiB から canonical 書き出し約220 MiB を引くと、約65 MiB が残ります。
- これは文字列を UTF-8 に再エンコードしてからハッシュしている分と推定され、2回分で約130 MiB(約4%)になります。
- 書き出したバイトを直接ハッシュすれば凍結指紋は保てる可能性があります。
- 欠落している証拠: `FingerprintHash.sha256` のソースです。

### M4: ensureProperties の外にある呼び出し側の Set 構築

- SnapshotCodec.fs:2617–2618 や 2624 では、呼び出しごとに `set [...]` を構築しています。
- 要素ごとの reader も同様であれば、計測外のコストがあります。
- 定数のモジュールレベルへの巻き上げは、挙動を変えない最小施策です。
- 欠落している証拠: reader のソースです。
- なお #3 は open/decode にしか効かず、commit/simulate には効きません。本文にそう明記してください。

### M5: proposal-order の規模が不自然

- 表示名変更1件で 169.6 MiB、約125 ms です。
- 2758–2761行のコメントは「追加・編集・新たに曖昧化した関数」を対象とすると述べていますが、スナップショット全体を前計算している疑いがあります。
- 割合は低くても、修正が小さい可能性があります。
- 欠落している証拠: GraphOrder のソースです。

### L1: プローブのオーバーヘッドが検証行に入っている

- profiled − baseline の差は、検証で 6,720,312 B、総計で 6,721,336 B です。プローブ増分のほぼ全量が検証行に乗っています。
- 上位行は baseline の値(1779.6 MiB)を使うか、注記してください。

### L2: GC回数の表記

- `CollectionCount` は累積値です。「15 Gen0, 4 Gen1, 2 Gen2」は「GC 15回、うち gen1 以上が4回、うち gen2 が2回」と書くべきです。
- pause 比率 6% は別サンプルの中央値どうしの比です。サンプルごとでは 4.3〜7.2% です。

### L3: control と baseline の割当差

- control の割当が baseline を上回っています(1000: +158,832 B、3000: +472,992 B)。
- 原因は不明ですが無視できる大きさです。解釈しないでください。

### L4: source-check の性質

- source-check の 109.7 MiB は `ReadAllText` の約2倍コストとほぼ同じです。
- バイト比較にすれば削減できますが、比較はより厳格になります(BOM が差として検出される)。挙動変更として扱ってください。

## 主張の扱い

**変更が必要な主張**
- 改善順序の根拠(H2)。
- commit と simulate の関係の記述(H1)。
- GC回数の表記(L2)。
- 検証 1786.0 MiB への注記(L1)。
- 1行目の「model construction」は decode のコストであり、commit には該当しません。区別してください。

**確立している主張**
- 見出しの時間と割当。
- 同期帰属。
- 表の非重複性と算術。
- 正規化モデルが2回目の検証であること。
- ディスクが小さいこと。
- JSON 形式切替だけでは不十分であること。
- deep の下位コスト。

**追加の証拠が必要な主張**
- json.write の内訳(jsonBytes から解析的に確認し、曖昧ならプローブを分割)。
- simulate の内訳(既存データ)。
- setup のスケーリング(既存データ)。
- ホストの GC モード。
- #4 の補題。
- M3〜M5 で挙げた未提供ソース。

## 次の手順(優先順)

1. **既存データだけでできる確認**
   - simulate の帰属分析。
   - profiled の 1000/3000 で setup 時間を比較。
   - jsonBytes から json.write のコピー比率を算出。
2. **ホストの GC 設定を確認**し、異なれば control を再実行する。
3. **バイト同一の機械的施策を最初の実験にする**
   - writeSnapshotAt のバッファ処理。
   - Set の巻き上げ。
   - 検証は canonical JSON、record バイト、凍結指紋の一致で行う。
4. **#4 の補題検証スパイクを並行して行う**(機能テストのみ)。
5. **その後に #1**(global index、次に推論)、#5(proposal-order)を確認する。
