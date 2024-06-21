<a name="readme-top"></a>

> [!CAUTION]
> All plugin credits go to [K4ryuu](https://github.com/K4ryuu)! All I did was make a few small changes for it to work with SharpTimer.

<!-- PROJECT LOGO -->
<br />
<div align="center">
  <h1 align="center">KitsuneLab©</h1>
  <h3 align="center">SharpTimer Map Record List</h3>
  <a align="center">A simple list to display the top map times for the active map on your map walls. Dynamically updates on given interval or if its disabled on round starts.</a>
  <img src="https://files.catbox.moe/pkarm0.png" alt="" style="margin: 0;">

  <p align="center">
    <br />
    <a href="https://github.com/M-archand/SharpTimer-WallLists/releases/">Download</a>
  </p>
</div>

<!-- ABOUT THE PROJECT -->

### Dependencies

To use this server addon, you'll need the following dependencies installed:

- [**CounterStrikeSharp**](https://github.com/roflmuffin/CounterStrikeSharp/releases): CounterStrikeSharp allows you to write server plugins in C# for Counter-Strike 2/Source2/CS2
- [**K4-WorldText-API**](https://github.com/K4ryuu/K4-WorldText-API): This is a shared developer API to handle world texts (even multiline, dynamically changing and config saved ones) easily, but in the other hand end users can also use it to spawn world texts as they want and manage them per map.
- [**SharpTimer**](https://github.com/Letaryat/poor-sharptimer): SharpTimer is a "simple" Surf/KZ/Bhop/MG/Deathrun/etc. CS2 Timer plugin using CounterStrikeSharp.
  
<p align="right">(<a href="#readme-top">back to top</a>)</p>

<!-- ROADMAP -->

## Commands (root)

- !maplist - Create a points list in front of the player and save it to config file.
- !maprem - Remove closest points list from your position. (100 units max) (CURRENTLY BROKEN & I'm too dumb to fix it :P)

<!-- ROADMAP -->

## Roadmap

- [X] Update for SharpTimer usage.
- [X] Add color configs. See [here](https://i.sstatic.net/lsuz4.png) for color names.
- [X] Ability to truncate player name.
- [ ] Fix !maprem

<p align="right">(<a href="#readme-top">back to top</a>)</p>

<!-- LICENSE -->

## License

Distributed under the GPL-3.0 License. See `LICENSE.md` for more information.

<p align="right">(<a href="#readme-top">back to top</a>)</p>
