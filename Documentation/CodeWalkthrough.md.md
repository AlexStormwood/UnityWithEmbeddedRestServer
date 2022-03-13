# Code Walkthrough

This Unity project contains some modifications to a standard, barely-touched Unity project made with the 3D (Core) Project template.

## The Game's One Scene

The project contains the usual SampleScene file found within `Assets/Scenes/` and uses that as the level to demo all functionality.

As the project is not involving any actual gameplay or visuals, the Main Camera and Directional Light are untouched.

There is a "Dummy Data Manager" gameobject in the SampleScene that essentially acts as a data structure, set up in a way that other gameobjects can access its data during the game's runtime.