# LWF Raven QoL（Thunderstore 用 README の日本語対訳）

同梱しない。英語版 `README.md` の中身を確認するための控え。

---

# LWF Raven QoL

**Lazy Witch's Factory** の運送レイヴンの QoL MOD。

## ドラッグ敷設

- レイヴンを置いて、ドラッグして、離す
- 運搬範囲いっぱいの間隔で「荷下ろし地点＋レイヴン」が並ぶ

![ドラッグ敷設](動画: chain-drag.webp)

## 自動配置

- レイヴンを置いて、範囲外の配送地点をクリック
- 資源・設備・未購入の土地を避ける
- 資源・設備・未購入の土地を避けて自動で並べる

![自動配置](動画: chain-click.webp)

## 配送先の変更

- レイヴンにカーソル → 「荷下ろし地点設定切り替え」（既定 Tab）
- クリック／共有／ドラッグで敷き直し
- 元の荷下ろし地点は残る

![配送先の変更](動画: retarget.webp)

## 中身の表示

- レイヴンや荷下ろし地点にホバー → アイコン × 個数

![ホバーで中身](画像: stock-hover.png)

## マップビュー

- 左上に：モモコの再投下／在庫の札／左の一覧の切り替え
- 操作説明とスポンサーは消える
- 魔法を構えている間は魔法の枠だけ出る

![マップビューのボタン](動画: mapview.webp)

## 中間地点の追加

- マップビューの視点移動に「土地の中間」を追加（所有済みの土地の間だけ）
- 境界で再投下すると着地点も境界
- 押しっぱなしで連続移動

![中間地点の追加](動画: map-halfstep.webp)

---

設定は `BepInEx/config/kiyonakanata.lwfravenqol.cfg`。詳しくは [GitHub](https://github.com/KiyonakaNata/lwf-raven-qol)。

Lazy Witch's Factory **ver 0.27.0** で動作確認。非公式のMOD、公式のサポート対象外。
