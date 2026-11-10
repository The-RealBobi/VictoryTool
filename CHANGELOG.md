# VictoryTool changelog

## v1.4.1

- Hid special technique kits with incomplete main slots and selected the first complete kit when opening a character.
- Hid the variant picker in the editor and on the player card when fewer than two complete kits were available, and used the same localized labels in both views.
- Labeled regular kits **Normal** and preserved hidden special kits when applying a technique template to all visible variants.
- Filled missing Japanese-origin names from the game's romanized names when creating a character from the catalog or reopening a source-based package. Existing names were preserved.
- Fixed discarded edits reappearing in the roster and discarded editor sessions reopening on restart.
- Moved the localized language label next to the locale picker and aligned the roster search placeholder.
- Updated the Spanish label for unaffiliated teams to **No afiliado**.

## v1.4.0

- Fixed EVENT animation exports that were generated but not selected by the game.
- Fixed missing shared CHR animation banks, which could leave a character frozen after an EVENT animation ended.
- Fixed batch animation export failures when several custom characters shared an EVENT bank and used overlapping bone names.
- Corrected EVENT movement offsets caused by applying custom character proportions to scene movement.
- Improved support for animation profiles prepared from A-pose and mixed-pose (A & T-posed at the same time) models (yes, this exists).
- Improved animation export time with parallel processing of banks and clips.
- Reduced generated animation file sizes by sharing identical channel data.
- Improved custom model registration and companion-file handling, including generating required object metadata when the source character lacks some data.
- Fixed custom uniform and shoe selection, including cases where choosing a new item retained the source character’s equipment.
- Change deliveries so that characters are all grouped in one single delivery.
- Allowed characters to be exported again after a completed export.
- Grouped batch configuration edits so shared tables are updated together, and moved expensive work off the interface thread.
- Fixed crashes and selections reverting when changing uniforms in the Appearance tab.
- Improved navigation between creation, modification and incorporation.
- Improved save and recovery handling.
- Added package thumbnails and improved package refresh handling in the incorporation list.
- Replaced the Generate animations button with a checkbox.
- Added search filters.
- Improved team filtering with searchable selections and removable tags. The search clears when selecting the last matching team.
- Improved portrait composition, skin tones, uniform compatibility, layer placement and support for special and armored portraits.
- Used the base character’s icons when saving a character without replacement icons.
- Hid special technique kits with incomplete main slots. Regular kits are labeled Normal, duplicate rarity names receive a numbered suffix, and variant controls are hidden when there is no choice to make.
- Made character names, positions, affinities, origin games and other interface text follow the selected language more consistently.
- Improved Japanese name display, reading annotations and romanized Japanese-origin names outside the Japanese interface.
- Resolved game text tags such as MNT and FLL into readable names, and improved description wrapping.
- Updated icons and language labels
- Reduced logging to errors and several specific logs.
- Implemented the CFGBINManager library.
