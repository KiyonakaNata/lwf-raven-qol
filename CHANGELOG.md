# Changelog

## 1.2.0 — 2026-09-13

- レイヴンを撤去しても、荷が残っている荷下ろし地点は残る（`Keep a dispatch port that still holds items`）
- A Dispatch Port that still holds items stays when its Raven is removed

## 1.1.1 — 2026-09-13

- マップビューで魔法を構えている間、魔法の枠（名前・コスト・対象）が出る
- 自動配置が未購入の土地を避けて曲がる
- 遠くを一回クリックするだけで最後まで敷く
- 工房などの上で離したときの吸い付きを、当たり判定で拾う
- The wand panel shows in map view while a wand skill is selected
- Auto placement bends around unowned land
- A single click far away lays the whole line
- Dropping the line on a crafter snaps to its input more reliably

## 1.1.0 — 2026-09-07

- 自動配置: レイヴンを置いて範囲外をクリックすると、そこまで自動で並べる
- マップビューの移動に土地の中間（境界）を追加。境界で再投下すると着地点も境界
- Auto placement: click beyond the range and the line is laid for you
- Map view moves in half steps; redeploy from a boundary lands on the boundary

## 1.0.2 — 2026-09-07

- 終端の荷下ろし地点の向きが本体のスナップに従う
- 途中の岩・設備・未購入の土地は手前に寄せて置く
- マップビューの押しっぱなし移動
- The last Dispatch Port faces the way the game's snap decides
- Rocks, buildings and unowned land along the way are stepped back from
- Hold to keep moving in map view

## 1.0.1 — 2026-09-01

- Thunderstore の説明を箇条書きに
- Thunderstore page rewritten as bullet points

## 1.0.0 — 2026-09-01

- 初版。中継線のドラッグ敷設・配送先の変更・中身の表示・マップビューの手入れ
- First release: drag to lay relay lines, retarget a Raven, stock display, map view tweaks
