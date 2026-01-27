## v.0.2.12.1

- fixed a rare issue causing the game to crash (on load or in-game) after loading/creating a bike connection between a bus lane and a non-bus lane.
- fixed a rare issue corrupting custom connections data if a source segment was split
- fixed a rare issue corrupting custom connections data if a source segment was replaced by merging short segments (the game may replace two short segments with one longer one if it determines the node connecting them is no longer needed)
- fixed an issue with the Reset to vanilla action when data contains broken custom lane connections (the previously mentioned corrupt lane connection data)