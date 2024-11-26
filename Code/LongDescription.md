# Traffic

## Successor to TM:PE for Cities Skylines II

The goal of the project is to bring more tools to the game that could help users manage traffic, well known from CS1 TM:PE mod and new ones.

### Included features

- **Lane Connector Tool** - no need to describe it for users familiar with TM:PE in CS1, but for new players, it's a tool which allows for changing lane connections at intersection to any that suits your use case. Since the mod cannot read the user's mind, in certain conditions it may require revisiting the modified intersection to add or change connections. More details below.
- **Priorities Tool** - a tool that allows for changing lane priority at intersection to any that suits your use case. Since the mod cannot read the user's mind, in certain conditions it may require revisiting the modified intersection to reapply the change.

### Usage

**Ctrl+R** enable **Lane Connector Tool** or simply click on the button in top left corner, then select any intersection.

Change lane connections by selecting source and target lane circles.

For more advanced setups:
- hold **Alt** to change the mode to _**unsafe** connection (dashed)_ - doesn't work for track connections
- hold **Ctrl** for _**track-only** connection_,
- hold **Shift** for _**road-only** connection_
- hold **Ctrl+Shift** for _**shared-only** connection_ (e.g.: Car+Tram)

#### Known issues and limitations (in the current version)

- No support at intersections with _the roundabout upgrade_, custom lane connections has to be reset first to apply the upgrade
- No support for vanilla _forbidden direction_ upgrades (tooltips with more information is available in game)
- No support for managing bidirectional lane connections (e.g.: two-way single train/tram track connections)
- Intersection with custom _Lane Connections_ will not generate lane connections when modified by e.g.: adding new intersecting road - connections need to be created manually or simply reset

Custom _Lane Connections_ may be automatically removed (leaving lanes not connected!) in following conditions:
- when the network composition has changed e.g.: "small two-way road" was replaced with asymmetric 2+1 variant
- road direction has changed with help of replace tool
- tram track upgrade has been applied or removed

Issues and limitations above are possible to solve, but they require extensive effort which would delay the release of the mod, so I decided to reduce the initial scope of compatibility features. I hope to improve them later, with your help.

### Compatibility with other modifications

- Automatic data migration from _**Traffic Lights Enhancement's** Lane Direction_ tool into Traffic's Lane Connections (a message dialog will appear with migration results when loading a savegame)
- Tested with _**Extended Road Upgrades**_, no issues found.
- Selecting a road (or all instances) with _**Road Builder**_ mod will reset _Traffic's_ custom priorities and lane connections

### Saving

The mod is storing the data inside the savegame file, but thanks to how CS II is handling missing mods and assets, savegames will load correctly but the changes applied by the mod will reset once a road/rail network update is triggered, resetting them to the defaults.

### Editor

The mod was not tested thoroughly in the map editor, but is expected to save the _Lane connector_ and _Priorities tool_ settings data into the map file which should later load successfully when a new game is created from it.
Opinions/tests greatly appreciated.

### Plans

Multilanguage translation support is handled via Crowdin, link in the mod description.
In case of critical issues, small bugs fixes or tweaks they will be released more frequently.

### Feedback, Features Ideas, Support

You can contact me on _TM:PE_ or _Cities: Skylines Modding_ discord and PDX forum in the comment section of the mod, but for easier management, for larger feature requests or anything that requires more  discussion, I'd prefer GitHub's _Issues_ or _Discussions_

### Credits

- REV0 - the main mod logo,
- Bad Peanut - in-game button menu icon,
- Chameleon TBN - testing and feedback,
- Algernon, Klyte45, Quboid, T.D.W. and yenyang - Cooperative development, testing and feedback.
- Slyh (Traffic Lights Enhancement) - help with compatibility and mod details for lane data migration tool
