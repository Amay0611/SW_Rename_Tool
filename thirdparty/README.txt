# SolidWorks Interop アセンブリ配置フォルダ

以下の2ファイルを、お使いのSolidWorksインストールフォルダからこのフォルダにコピーしてください。

  <SolidWorksインストールフォルダ>\api\redist\SolidWorks.Interop.sldworks.dll
  <SolidWorksインストールフォルダ>\api\redist\SolidWorks.Interop.swconst.dll

典型的なインストールフォルダの例:
  C:\Program Files\SOLIDWORKS Corp\SOLIDWORKS\api\redist\

コピー後、このフォルダの中身は以下のようになります。
  thirdparty\
    SolidWorks.Interop.sldworks.dll
    SolidWorks.Interop.swconst.dll

これらは.NET Framework 4.0以降向けのPrimary Interop Assembly（公式のビルド済みDLL）なので、
tlbimpによる型ライブラリインポートは不要で、通常のDLL参照として dotnet build でそのまま使えます。

インストールフォルダから直接参照せず、このフォルダにコピーしてから参照するのは、
別のPCで開発する場合やSolidWorksの再インストール時に参照切れを起こさないための一般的な作法です。
