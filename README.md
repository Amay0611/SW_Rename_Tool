# SwRenameTool

SolidWorksの参照関係（アセンブリ・パーツ・図面）を維持したまま、構想設計時の仮称ファイル名を一括リネームするWPFデスクトップアプリの雛形です。

## 構成

```
SwRenameTool.sln
thirdparty/               … SolidWorks Interopアセンブリ配置フォルダ（要手動配置、README参照）
src/
  SwRenameTool.Core/       … SolidWorks API連携・参照解析・検証・バックアップ・リネーム実行ロジック（UI非依存）
    Models/
      FileType.cs
      FileNode.cs          … 参照グラフのノード（Parents/Childrenを保持）
      ValidationResult.cs  … Dry Run結果
      RenameLogEntry.cs    … 実行ログ1行分
    Services/
      ISolidWorksSession.cs / SolidWorksSession.cs         … SolidWorksのバックグラウンド起動管理
      IToolboxDetector.cs / ToolboxDetector.cs             … レジストリからToolboxパスを検出
      IReferenceGraphBuilder.cs / ReferenceGraphBuilder.cs … GetDependencies2による参照解析
      IDryRunValidator.cs / DryRunValidator.cs             … ロック・重複・範囲外参照のチェック
      IBackupService.cs / BackupService.cs                 … 差分フォルダコピー＋復元手順生成
      IRenameExecutionService.cs / RenameExecutionService.cs … IPackAndGoによるリネーム実行
      IRenameLogger.cs / RenameLogger.cs                   … CSVログ出力
  SwRenameTool.App/        … WPFアプリ（MVVM, CommunityToolkit.Mvvm使用）
    MainWindow.xaml(.cs)
    ViewModels/
      MainViewModel.cs
      FileRowViewModel.cs
tools/
  PackAndGoPoc/            … SolidWorks API技術検証用の独立コンソールアプリ
```

## 前提・セットアップ

1. **SolidWorks本体が開発機にインストールされていること**（Professionalライセンス）。
2. **SolidWorks Interopアセンブリを配置する。** `thirdparty`フォルダに以下の2ファイルを、
   お使いのSolidWorksインストールフォルダからコピーしてください（詳細は`thirdparty/README.txt`）。
   ```
   <SolidWorksインストールフォルダ>\api\redist\SolidWorks.Interop.sldworks.dll
   <SolidWorksインストールフォルダ>\api\redist\SolidWorks.Interop.swconst.dll
   ```
   典型的なインストールフォルダ例: `C:\Program Files\SOLIDWORKS Corp\SOLIDWORKS\`
3. **ビルドは `dotnet build` / VSCode で完結します。**
   ```
   dotnet build SwRenameTool.sln
   ```
4. 本コンテナはLinux環境かつSolidWorks非搭載のため、ここではビルド・実行確認ができていません。
   実機のWindows開発機での動作確認が必須です。

## なぜこの方式（Interopアセンブリの直接参照）にしたか

この結論に至るまで、3つの方式を順に試しました。

1. **`<COMReference>`によるtlbimpベースの型ライブラリインポート**：
   `ResolveComReference`というMSBuildタスクを必要とし、これは**.NET Framework版のMSBuild
   （Visual StudioのIDEビルド）でしか動作せず**、VSCode + `dotnet` CLIではビルドできませんでした。

2. **`dynamic`キーワードによる後期バインディング**：
   型ライブラリ参照は不要になりましたが、実機検証で
   `COMException (0x8002802B): TYPE_E_ELEMENTNOTFOUND`が発生し動作しませんでした。
   C#の`dynamic`は.NET Core/.NET 5以降、内部的に`IDispatch::GetTypeInfo`の成功を要求するため、
   これに完全準拠していないCOMオートメーションサーバー（SolidWorksを含む）で失敗します。

3. **`Type.InvokeMember`によるリフレクションベースの後期バインディング**：
   `GetTypeInfo`を必要としない方式に切り替え、`GetDependencies2`や`OpenDoc6`等は正常に動作する
   ことを確認しましたが、**`GetPackAndGo`は後期バインディングでは動作しませんでした**
   （`DISP_E_PARAMNOTOPTIONAL`や`DISP_E_TYPEMISMATCH`が発生）。VBAで`Dim As Object`の後期バインディングを
   使っても同様に失敗し、`Dim As SldWorks.PackAndGo`等の**早期バインディング**でのみ成功することを
   実機検証で確認しました。つまりこれはSolidWorks側の実装上の制約であり、.NET側の工夫では回避できません。

**最終的な解決策**：SolidWorksインストールフォルダの`api\redist`には、
`SolidWorks.Interop.sldworks.dll` / `SolidWorks.Interop.swconst.dll`という、
SolidWorks公式のビルド済みPrimary Interop Assembly（PIA）が同梱されています。
これは既に他のPCでコンパイル済みの通常の.NET DLLなので、`tlbimp`によるビルド時生成が不要（＝
`ResolveComReference`タスクも不要）で、通常の`<Reference>`として`dotnet build`でそのまま参照できます。
かつ早期バインディングなので、`GetPackAndGo`のような後期バインディングで問題を起こすAPIも
問題なく呼び出せます。tlbimpの制約と後期バインディングの制約、両方を同時に回避できる方式です。

## 実装状況

- `ReferenceGraphBuilder.Build()`: 実装済み。**設計を`GetDependencies2`ベースから
  FeatureManagerツリー走査ベースに変更した。** 当初は`GetDependencies2`（全階層をフラットに
  返すAPI）を使い、見つかったサブアセンブリそれぞれに対しても同じ呼び出しを行い、その差分から
  親子関係を組み立てる方式にしていたが、これには2つの問題があった。
  1. 一覧の並び順がSolidWorksのデザインツリーの表示順と一致しない。
  2. 3階層以上のネストで、孫パーツが正しい階層（本来の親のすぐ下）ではなく、祖先アセンブリの
     直接の子として扱われてしまう（`GetDependencies2`は常に全階層をフラットに返すため）。

  そのため、`ModelDoc2.FirstFeature`/`Feature.GetNextFeature`/`Feature.GetSpecificFeature2`で
  `Component2`を辿るFeatureManagerツリー走査方式（`WalkDesignTree`）に変更した。これは
  SolidWorksのデザインツリーが実際に表示する順序そのものであり、かつ真の親子階層を正確に
  反映する。サブアセンブリへの再帰時は、`Component2.GetModelDoc2()`で既にメモリ上に
  ロード済みのドキュメントを取得できることが多く、追加の`OpenDoc6`が不要になる
  （軽量読み込み等で未解決の場合のみ、フォールバックとして明示的に開く）。
  `MarkOutOfScopeReferences`（対象フォルダ内の他アセンブリの高速スキャン）では、
  順序を必要としないため引き続き`GetDependencies2`ベースの`OpenAndGetFlatDependencies`を使う。
- **一覧の並び順（ツリー順表示）**: `MainViewModel.BuildTreeOrderedRows`が`FileNode.Children`を
  深さ優先探索して並び替える設計は、上記の`WalkDesignTree`への変更により、`Children`の内容
  そのものがSolidWorksのデザインツリーと一致する正確なものになったため、実質的に
  SolidWorksのデザインツリー順の表示になった。
- `ReferenceGraphBuilder.MarkOutOfScopeReferences()`: 実装済み・実機動作確認済み。対象アセンブリと
  同じフォルダ内の「今回のスコープに含まれない他のアセンブリ」を軽くスキャンし、今回のグラフに
  含まれる部品を参照していないか確認する。該当する部品は`IsReferencedFromOutsideScope = true`になる。
  **仕様変更**: 当初は範囲外参照のある部品をリネーム対象から自動除外していたが、
  外部参照元（Parents）が判明しているのであれば、選択アセンブリ自体のケースと同様に
  参照元の参照を更新すれば対応可能という指摘を受け、除外をやめてリネーム可能にした。
  `FileNode.IsExcluded`は現在`IsToolboxPart`のみで判定し、`IsReferencedFromOutsideScope`は
  UI上の情報表示（「外部参照あり（自動更新）」）およびリネーム後の参照更新要否の判定にのみ使う。
  **ただし、ユーザーが選択した最上位アセンブリ自体（サブアセンブリを直接選んだ場合を含む）は、
  この情報表示の特別扱いはせず、通常のノードと同じ扱いにしている。** 当初は最上位アセンブリ自身が
  外部から参照されている場合、ツール自体が「対象外」としてリネームできなくなる不具合があった。
  サブアセンブリ単位でのリネームができないと運用上の制約が大きいという指摘を受けて修正した。
  **さらに、選択したアセンブリBの祖先アセンブリX（Bを含む上位アセンブリ）がBの子孫パーツを
  「参照している」ように見えるケースも、独立した外部参照としては扱わない。** `GetDependencies2`は
  全階層をフラットに返す仕様のため、Xの依存関係一覧にはBを経由した子孫パーツも自動的に含まれてしまう。
  これは独立した外部参照ではなく、X→Bの参照さえ更新すれば自動的に解決される間接参照のため、
  Xがtopアセンブリ自体も参照している場合は、その配下ノードのParents記録をスキップする。
  一方、Xがtopアセンブリを参照しておらず、スコープ内の別ノードを直接参照している場合
  （`SharedPart`のような真の共有部品）は、`IsReferencedFromOutsideScope`をtrueにしてParentsに記録する。
- **外部参照元アセンブリの参照更新（`RenameExecutionService`）**: 実装済み。当初は選択した
  最上位アセンブリ自体のみが対象だったが、スコープ内の任意のファイル（例: サブアセンブリの
  部品など）が、今回のスコープ外のアセンブリ（A）から直接参照されている状態でリネームした場合も、
  同様にAの参照が古い名前のまま残ってファイルが孤立してしまうため、この更新処理を
  「リネームに成功し、かつスコープ外のParentsを持つノード全て」に一般化した。
  **さらに、判定基準を「親がスコープ外かどうか」から「親自身がリネームされたかどうか」に
  変更した。** 選択アセンブリ自体（例: `Assem1`）がスコープ内であっても、それ自身は
  リネームされず、配下の部品だけをリネームしたケースで、`Assem1`の内部参照が更新されない
  不具合が実機で見つかったため。`SetDocumentSaveToNames`で新旧同名を指定したファイルは、
  Pack and Goが実際には保存し直さず、内部の参照テーブルも更新しないためと考えられる。
  現在は「参照している親自身がリネームされていない」場合、範囲の内外を問わず
  `ReplaceReferencedDocument`で更新する。あわせて、Pack and Go処理中にSolidWorksが
  内部的にスコープ内の他ドキュメントを開いたままにしている可能性があるため、
  参照更新の前にスコープ内全ファイルへの`CloseDoc`も試みるようにしている（開いていなければ
  何もしないので安全）。
  `ISldWorks.ReplaceReferencedDocument(参照元パス, 旧参照パス, 新参照パス)`というAPIが
  「参照元ドキュメントが閉じていること」を前提に外部からの参照を書き換えられるため、
  `SavePackAndGo`成功後・旧ファイル削除前のタイミングで、対象ノードごとに参照元
  アセンブリそれぞれに対してこれを呼び出し、参照を新しい名前に更新する。
  参照元アセンブリが（ユーザーが手動で）開かれている場合は更新に失敗するため、その場合は
  ログにエラーとして記録される（自動再試行等は行わない）。
- `RenameExecutionService`（`IPackAndGo`によるリネーム実行）: 実装・実機確認とも完了。
  `GetPackAndGo`→`GetDocumentNames`→`SetDocumentSaveToNames`→`SavePackAndGo`の一連の流れを
  実機で実行し、パーツ・アセンブリ・図面あわせて15ファイルのリネームと参照関係の維持
  （SolidWorksでツリーを開き直して確認済み）を確認した。
- **WPF本体（`MainViewModel`/`MainWindow`）**: `Core`の各サービスと結線済み。
  アセンブリ選択→参照解析→一覧表示→Dry Run→バックアップ＆実行、の一連の流れが動作する。
  図面の自動追従（`FileRowViewModel.NewBaseNameEdited`イベント経由で`MainViewModel`が
  紐付いた図面行に反映、「連動解除」チェック時は追従しない）も実装済み。
  Dry Run結果は`DryRunMessages`（警告・エラーの文字列一覧）としてUIに表示される。
- **サムネイル表示**: 実装済み。当初はSwDM APIの`GetPreviewBitmap`を想定していたが、
  SwDM API自体を開発者キーが取得できず断念した経緯があるため、Windows Shell API
  （`IShellItemImageFactory`、P/Invoke経由）でサムネイルを取得する方式にした。
  SolidWorksはエクスプローラー向けにサムネイルハンドラーを登録しているため、
  SolidWorksを起動せずに取得できる。一覧の表示をブロックしないよう、行を表示した後に
  1件ずつ非同期で読み込む（`MainViewModel.LoadThumbnailsAsync`、`await Task.Yield()`で
  UIスレッドへ随時制御を返す）。選択した行は右側のプレビューパネルに拡大表示される。
  拡大プレビューは一覧の小サムネイルをただ引き伸ばすのではなく、選択されたタイミングで
  Windowsエクスプローラーの「特大アイコン」相当（256px）の解像度で別途取得する
  （`FileRowViewModel.LargeThumbnail`）。
- **一覧の並び順（ツリー順表示）**: 実装済み。当初は`ReferenceGraphBuilder`が返す一覧の順序
  （内部的にDictionaryから生成されるため親子関係とは無関係）のままだったが、親子関係が
  分かりにくいという指摘を受け、選択アセンブリを起点に深さ優先探索で並べ替え、階層に応じて
  左マージンでインデントを付ける表示に変更した（`MainViewModel.BuildTreeOrderedRows`、
  `FileRowViewModel.IndentLevel`/`IndentMargin`）。図面は対応する3Dモデルの直後・同じ階層に
  配置する。複数の親を持つ共有部品は、最初に辿り着いた親の下に1回だけ表示する
  （行を複製すると編集の同期は取れるが表示が紛らわしいため。参照元数は「参照」列で確認できる）。
- **SolidWorksとのCOM通信は専用STAスレッドに固定**（`SolidWorksSession`が内部で1本の
  専用スレッドを起動し、`Dispatcher`のメッセージポンプ経由で呼び出しを中継する）。
  当初`Task.Run`でThreadPoolのスレッドから都度呼び出す実装にしていたところ、
  WPFアプリが「参照関係を解析しています」と表示した直後にクラッシュする現象が実機で
  発生した。PackAndGoPoc（単一スレッドで一貫して安定動作）との違いから、複数の異なる
  スレッドからSolidWorksのCOMオブジェクトを呼び出すことが原因と判断し、この対策を入れた。
  `SwRenameTool.Core.csproj`に`<UseWPF>true</UseWPF>`を追加しているのはこの
  `System.Windows.Threading.Dispatcher`（WindowsBase）を利用するため。

## 検証方法

### 1. 参照グラフ構築の検証（graph コマンド）

範囲外参照が正しく検出されるか確認するには、以下のようなテストデータを用意してください。

```
C:\SwTest\
  Assem1.sldasm          … 今回選択する最上位アセンブリ
  PartA.sldprt           … Assem1が参照する通常のパーツ
  SharedPart.sldprt      … Assem1と、下記OtherAssem.sldasmの両方から参照される共有部品
  OtherAssem.sldasm      … Assem1とは無関係な別のアセンブリ。SharedPart.sldprtを参照している
```

```
PackAndGoPoc.exe graph "C:\SwTest\Assem1.sldasm"
```

`graph`コマンドは以下を順に表示します。読み取りのみで、ファイルへの変更は行いません。
1. 最上位アセンブリの全体依存関係（`GetDependencies2`の1回呼び出し）
2. サブアセンブリごとの直接の子関係の構築結果
3. 範囲外アセンブリのスキャン結果（`SharedPart.sldprt`が検出されれば「範囲外参照のため対象外」と表示される）
4. 最終的にUIの一覧に出す想定のファイル一覧（親ファイルの表示付き）

**実機確認済み**：サブアセンブリ配下パーツの親子関係、および範囲外参照の検出、いずれも想定通り動作。

### 2. Pack and Go の検証（packandgo コマンド）

```
PackAndGoPoc.exe packandgo "C:\SwTest\Assem1.sldasm"
```

依存ファイル一覧を表示し、確認プロンプトで `y` を入力するまでは実際の保存を行いません。
**検証用のコピーフォルダで実行してください（本番データに直接使わないこと）。**

### 3. GetDependencies2単体の検証（deps コマンド）

```
PackAndGoPoc.exe deps "C:\SwTest\Assem1.sldasm"
```

**実機確認済みの挙動**：
- `GetDependencies2` の戻り値は `{名前, フルパス, 名前, フルパス, ...}` のペア構造
- サブアセンブリ配下のパーツも、最上位アセンブリへの1回の呼び出しで全階層フラットに含まれる
  （ただし親子構造自体は分からないため、`graph`コマンドのようにサブアセンブリごとに
  同じ呼び出しを行って差分を取る必要がある）
- 図面(.slddrw)はこの一覧に含まれない（同一フォルダ内の同名ファイル探索で紐付け）
- Toolbox部品がパスに含まれるかどうかは、実際にToolboxから参照している構成でまだ未検証

## Pack and Goについて判明した設計上の重要な点

1. **IPackAndGoは1ファイルずつではなく、アセンブリ全体を1回で処理する構造**
   最上位アセンブリを開いた時点で依存ファイル一覧（`GetDocumentNames`）が確定し、
   それと同じ順序・件数の新しい名前配列を`SetDocumentSaveToNames`に渡し、
   `SavePackAndGo`を1回呼ぶことで全ファイルの名前変更と参照更新が一括実行される。
   そのため`RenameExecutionService`は、アセンブリ単位で1回だけ実行する構造にしている。

2. **キャンセル可能なタイミングが「ファイル単位」ではなく「実行開始前まで」に限定される**
   `SavePackAndGo`の呼び出し自体は分割できないため、「安全なタイミング（ファイル単位）での
   キャンセル」は、実際には「Pack and Go実行前まで」が限界になる。実行が始まったら完了を待つ形に
   なる点は、UI上のキャンセルボタンの説明文言などで利用者に伝える必要がある。

3. **対象外ファイルは「元の名前のまま配列に含めて渡す」**
   Toolbox部品や範囲外参照のファイルも`GetDocumentNames`の一覧には含まれるため、
   `SetDocumentSaveToNames`ではこれらの要素だけ元の名前をそのまま設定し、対象ファイルのみ新しい名前を
   設定する（配列から除外すると数・順序がズレてエラーになるため、除外という操作はできない）。

4. **同一フォルダ内でのリネーム（別フォルダへの複製ではなく上書き）は可能**
   Pack and Goは本来「別フォルダに複製する」機能だが、`SetDocumentSaveToNames`で指定するパスを
   元と同じフォルダ・別ファイル名にすることで、複製ではなく実質的な「その場でのリネーム」として使える
   （`SetSaveToName`でフォルダを一括指定する呼び出しは行わないこと。個別パス指定が上書きされるため）。

5. **保存前チェック（`GetDocumentSaveToNames`の戻り値ステータス）で重複名等を事前検知できる**
   `SavePackAndGo`を呼ぶ前に`GetDocumentSaveToNames`のステータス配列を確認することで、
   `swPackAndGoSaveStatus_UserInputNotCorrect`や`swPackAndGoSaveStatus_FileAlreadyExist`等のエラーを
   事前に検出できる。Dry Runフェーズの重複名チェックと合わせて、実行直前にも二重チェックする設計。

6. **`GetPackAndGo`は早期バインディングでのみ動作する**
   後期バインディング（`dynamic`、`Type.InvokeMember`、VBAの`Dim As Object`のいずれでも）では
   `DISP_E_PARAMNOTOPTIONAL`や`DISP_E_TYPEMISMATCH`が発生し失敗する。
   `Dim As SldWorks.PackAndGo`のような早期バインディングでのみ成功することを実機で確認した。

## 既知の注意点

- **軽量読み込み（Lightweight Components）が有効だと、`Component2.GetModelDoc2()`が
  `null`を返すことがある（公式仕様）。** `WalkDesignTree`実装後、実機で
  「サブアセンブリ自体は表示されるが、その配下のパーツが検出されない」不具合が発生した。
  軽量状態のコンポーネントはモデルドキュメントを持たないため、フォールバックの
  `OpenDoc6`頼みになるが、大規模アセンブリではネストした孫コンポーネントまで
  連鎖的に軽量状態になり得るため、`SolidWorksSession`でSolidWorks接続時に
  `SetUserPreferenceToggle(swAutoLoadPartsLightweight, false)`を設定し、
  このツールで開くアセンブリは常に完全解決（非軽量）で読み込むようにして対策した。
- **`ToolboxDetector`が当初、誤ったレジストリキーを参照していた。**
  `Software\SolidWorks\Applications\Toolbox\BrowserPath`という架空のキーを見ており、
  常にToolboxパス検出0件（＝Toolbox部品が一切除外されない）状態だった。
  正しいキーは`HKEY_CURRENT_USER\Software\SolidWorks\SOLIDWORKS <年度>\General`の
  `Toolbox Data Location`値（公式のToolbox移設手順等で確認）。年度部分はインストールされている
  SolidWorksのバージョンにより異なり、複数バージョンが同居している場合もあるため、
  `Software\SolidWorks`直下のサブキーを走査して該当するものを全て拾う実装にした。
  実機で正しいパスが検出されることを確認済み（起動時確認用MessageBoxは削除済み）。
- **Toolbox部品の判定は、パスベース（レジストリ）に加えて`IModelDocExtension.ToolboxPartType`
  （標準API、SolidWorks 2014〜）による判定も併用している。**
  `sldsetdocprop.exe`でToolbox化された部品は、実際のファイルの置き場所がToolboxフォルダ配下と
  限らない（内部フラグのみでToolbox扱いになる）ため、パスベース判定だけでは検出できない。
  `model.Extension.ToolboxPartType`（0=Toolbox部品ではない、0以外=Toolbox部品。
  `swToolBoxPartType_e`で定義）を確認することで、ファイルの場所によらず検出できる
  （`ReferenceGraphBuilder.IsToolboxPart`）。なお、この判定に類似した「`IsToolboxPart`」という
  内部フラグはSwDM API（`ISwDMDocument.ToolboxPart`）でも取得できるとされるが、標準API側の
  `IModelDocExtension.ToolboxPartType`で同等の判定が可能なため、SwDM API（開発者キー未取得のため
  未使用）は不要だった。
  **性能面の注意**：この判定はファイルを一度開く必要があるため、パスベース判定（一致すれば
  ファイルを開かずに済む）よりコストが高い。パスベースで該当しなかったファイルは全て
  追加で開閉が発生するため、100部品規模のアセンブリでは解析時間が有意に伸びる可能性がある。
  必要であれば、設定でこのAPI判定のオン/オフを切り替えられるようにする、判定結果を
  キャッシュする等の最適化を今後検討する。
- **`SavePackAndGo`が返すステータス配列の順序は保証されていない可能性がある。**
  `GetDocumentNames`／`SetDocumentSaveToNames`／`GetDocumentSaveToNames`の3つは
  「順序・件数が一致する」と公式ドキュメントに明記されているが、`SavePackAndGo`の戻り値
  （ファイル名を伴わないステータス配列のみ）についてはそのような記載が見当たらない。
  実機で「実際にリネームしたファイルとは別の、無関係なファイルの結果としてログに記録される」
  という不具合が発生し、インデックスの暗黙の対応関係が崩れている可能性が濃厚となった。
  対策として、`SavePackAndGo`の戻り値は使わず、こちらで確実に把握している
  `originalPaths[i]`→`newPaths[i]`のペアごとに「新ファイルが実際にディスク上へ作成されたか
  （`File.Exists`）」を成否判定の基準にする方式へ変更した。SolidWorks側の内部処理順序に
  依存しないため確実。同様に、保存前チェック（`GetDocumentSaveToNames`）の結果も、
  返ってきた名前をキーにした逆引き辞書でこちらの`newPaths`と突き合わせる形にし、
  インデックスの暗黙の対応には頼らないようにしている。
  .NET側の参照カウントを減らすだけなので、参照解析のために開いたドキュメントは、
  SolidWorks上では開いたままになる。これを閉じずに放置すると、後続のDry Runの
  ファイルロック検知が「自分自身が開いているファイル」を誤って「別プロセスで開かれている」
  と警告してしまう（実機で確認）。`ReferenceGraphBuilder`では`CloseDocument`ヘルパー
  （`sldWorks.CloseDoc(model.GetTitle())`）で明示的に閉じるようにしている。
  **`RenameExecutionService`でも同様の問題があった**：`SavePackAndGo`実行後も
  リネーム対象のアセンブリが開いたままになり、旧ファイルがロックされ続けて
  旧ファイル削除（後述）が失敗する原因になっていた。`SavePackAndGo`直後に
  `sldWorks.CloseDoc(model.GetTitle())`で閉じるよう修正した。
- **`IPackAndGo`は「新しい名前でコピーを作成する」機能であり、元のファイルを削除する
  機能ではない。** 保存先を同一フォルダ・別名にしても、SolidWorks自身は古い名前のファイルを
  消してくれない。「その場でのリネーム」を実現するには、`SavePackAndGo`が成功した後に
  こちらで明示的に旧ファイルを`File.Delete`する必要がある（実機で確認。当初はこれを
  見落としており、リネーム後に新旧両方のファイルが残ってしまう不具合があった）。
  `RenameExecutionService`ではコピー成功後に旧ファイルを削除する処理を追加している。
- **ログ出力先はコンストラクタ固定ではなく、実行の都度渡す設計にすること。**
  当初`RenameLogger`の出力先をコンストラクタで固定していたところ、アプリの実行時
  カレントディレクトリ（ビルド出力フォルダ等、予測しづらい場所）に書き込まれてしまい、
  ユーザーがログを見つけられない不具合があった。バックアップフォルダと同じ場所に
  出力する設計（`BackupResult.BackupFolderPath`を`RenameExecutionService.ExecuteAsync`
  の引数として渡す）に修正した。
- **`IncludeDrawings`等の設定は、`GetDocumentNames`を呼ぶ「前」に行う必要がある。**
  後で設定すると、`GetDocumentNames`が返す一覧（＝新しい名前配列のベースにする一覧）と、
  実際の保存対象一覧（図面を含む）の件数が食い違い、`SetDocumentSaveToNames`が
  正しく機能しなくなる（`status=False`になる）ことを実機で確認した。
- `~$ファイル名`（SolidWorksがファイルを開いている間に生成されるロックファイル）は、
  `*.sldasm`等のワイルドカードスキャンで誤って引っかかることがあるため、ファイル名の先頭が
  `~$`かどうかで除外する処理を`MarkOutOfScopeReferences`・`graph`コマンド双方に入れている。
- **重複名チェックはファイル種別（拡張子）ごとに行う必要がある。** パーツと図面は
  同じベース名を意図的に共有する（例: `Bracket.SLDPRT`と`Bracket.SLDDRW`）ため、
  拡張子を無視してグループ化すると正常なペアを誤って重複と判定してしまう不具合が
  あった。`DryRunValidator.CheckDuplicateNames`ではグループキーに拡張子を含めている。
- `System.Collections.Generic.Dictionary`等の値コレクションに対する遅延評価のLINQクエリを
  `foreach`で回しながら、ループの中で同じDictionaryに要素を追加すると
  `InvalidOperationException`（列挙中にコレクションが変更された）になる。
  `ReferenceGraphBuilder.LinkDrawings`で実際に発生した。ループ前に`.ToList()`で
  確定させること。
- `swDocumentTypes_e`や`swPackAndGoSaveStatus_e`等の列挙型は、Interopアセンブリを正しく
  参照していれば通常のenumとして使える。数値のハードコードは不要。
- COMオブジェクトは使用後に`Marshal.ReleaseComObject`で解放すること（特にサブアセンブリを
  何度も開閉する`ReferenceGraphBuilder`では、解放漏れがあるとSolidWorksのメモリ使用量が
  増え続ける原因になる）。
- **SolidWorksとのCOM呼び出しは、WPFアプリでは複数の`ThreadPool`スレッドから交互に
  行うとクラッシュする。** `Task.Run`を使うたびに別スレッドになりうるため、
  `SolidWorksSession`が起動時に生成する1本の専用STAスレッドに、`Dispatcher`
  （`System.Windows.Threading`, WindowsBase）経由で全呼び出しを集約している。
  `SwRenameTool.Core.csproj`に`<UseWPF>true</UseWPF>`があるのはこのため。
  ただしこれによりSDKの暗黙的`using`のセットが変わり、`System.IO`が自動では
  使えなくなる副作用があるため、`System.IO`を使う全ファイルに明示的な`using`が必要。

## 合意済みの主な仕様

- 対象選択はフォルダではなく最上位アセンブリ(.sldasm)を1つ選ぶ方式
- 仮称パーツの自動判定・ハイライトは行わない（命名に法則性がないため）
- Toolbox部品はレジストリパスで自動判定し除外
- 選択アセンブリの依存範囲外から参照されている共有部品は自動除外
- バックアップは差分（変更対象＋参照元）のみのフォルダコピー（Zip化なし）
- リネーム実行のキャンセルは安全なタイミング（ファイル単位、ただしPack and Go実行前まで）でのみ可能
- 実行失敗時は自動ロールバックせず、エラー表示のうえ手動復元を促す
- CSVインポートは行わず、UI上で直接編集
- 図面はパーツ名に自動追従するが、行ごとに個別解除が可能
