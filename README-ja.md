# MG Sprays 1.0.2

[English](README.md) | 日本語

CS2の壁・床へ、Workshopのマテリアルをスプレーとして投影するCounterStrikeSharpプラグインです。

## 対応環境

[CounterStrikeSharp](https://github.com/roflmuffin/counterstrikesharp) が必要です。

**Windows x64・Linux x64**に対応する配布構成です。共通ZIPには両OS用のSQLiteライブラリを含みます。

| OS | SQLiteのネイティブライブラリ |
| --- | --- |
| Windows x64 | `runtimes/win-x64/native/e_sqlite3.dll` |
| Linux x64 | `runtimes/linux-x64/native/libe_sqlite3.so` |

WindowsではSQLite保存・依存ライブラリの読込を確認済みです。Linux用ライブラリの同梱と参照は確認しましたが、**Linuxサーバーでのゲーム内動作は未確認*です。
## 導入・更新

1. `addons`フォルダーを`game/csgo/`へコピーします。
2. 同梱されている`workshop`フォルダーを、サーバーアセットとして使用しているWorkshopにアップロードしてください。
3. サーバーを再起動するか、プラグインを再読み込みします。

**DLL単体ではなく、プラグインのフォルダー全体を配置してください。** 以下の依存ファイルも必要です。

- `Microsoft.Data.Sqlite.dll`
- SQLitePCLRaw関連DLL
- 使用するOSに対応したSQLiteライブラリ（上記の表を参照）

更新ファイルは既存のJSON・cfgを上書きしません。



## 個人設定の保存

SQLiteデータベースを次の場所に自動作成します。

```text
addons/counterstrikesharp/plugins/MgSprays/data/mgsprays.db
```

テーブル名は`player_settings`です。

| 列 | 保存内容 |
| --- | --- |
| `steam_id` | SteamID64。精度を保つため、`TEXT`型の主キーとして保存します。 |
| `spray_name` | `test`や`charlotte_heart`など、JSONで定義した`Id`。ランダム設定の場合は`NULL`です。 |
| `spray_volume` | 音量を表す0～100の整数。初期値は100です。 |

個人設定を変更した際に、そのSteamIDの行を作成・保存します。
未登録プレイヤーの初期設定は、**ランダムスプレー・音量100%**です。

## 設定

### CVar（cfg）

CVarは次のファイルで管理します。

```text
game/csgo/cfg/mgsprays/mgsprays.cfg
```

cfgはサーバープロセスごとに一度だけ自動実行され、マップ変更時には再読み込みされません。

### 画像・音声（JSON）

画像・音声設定は次のファイルで管理します。

```text
addons/counterstrikesharp/configs/plugins/MgSprays/MgSprays.json
```

JSONには`ConfigVersion`、`SpraySoundEvent`、`SpraySoundResource`、`Sprays`を記載します。

各画像は`Id`・`Name`・`Material`・`Width`・`Height`で定義します。幅と高さは画像ごとに設定してください。どちらも初期値は**48**、設定可能範囲は**1～256**です。

設定例は[examples/MgSprays.json](examples/MgSprays.json)を参照してください。

## プレイヤーコマンド

| コマンド | 動作 |
| --- | --- |
| `!sprays` | スプレー一覧を表示します。 |
| `!spray <id/name>` | スプレーを変更します。`Id`または`Name`で指定できます。 |
| `!sprays <id/name>` | 従来の選択方法でもスプレーを変更できます。 |
| `!spray` / `css_spray` | 壁・床にスプレーを貼り付けます。引数は指定しません。 |
| `!sprays none` / `!spray none` | スプレーをランダムに設定します。 |
| `!sprayvol <0-100>` | 自分に聞こえる全プレイヤーのスプレー音量を調節します。0でミュートします。 |
| `!sprayvol` | 現在の音量を表示します。 |

ラウンドを跨いでスプレーを引き継ぐかどうかは、`lp_mgspray_keep_across_rounds`で切り替えます。寿命切れ・置換・マップ変更・無効化ではスプレーを削除します。
詳しくは、CVarをご確認ください。

## 音声

同梱のSoundevents定義を使用する場合は、JSONに次の値を設定します。

```json
{
  "SpraySoundEvent": "LP.MGSpray.Paint",
  "SpraySoundResource": "soundevents/lp_mgspray.vsndevts"
}
```

すべての定義が同じ音声ファイルを参照し、音量ごとに受信者を分けます。独自のイベント名を使用する場合は、その名前に対応する音量別定義を用意してください。
