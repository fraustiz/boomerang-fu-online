# Boomerang Fu Online

Mod BepInEx 5 qui ajoute un multijoueur en ligne à Boomerang Fu (jusqu'à 4 joueurs, un par PC), avec des menus intégrés au jeu.

- **Steam en mode Spacewar** (par défaut) : parties et invitations entre amis Steam, même avec un jeu emprunté par la famille Steam.
- **IP directe** : LiteNetLib en UDP 7777, avec ouverture automatique du port de la box par UPnP.
- **L'hôte décide de tout** : déplacements, coups, morts, boomerangs, scores. Les autres joueurs affichent son état et lui envoient leurs commandes.
- **Arrivée et retour en cours de match**, synchronisation des menus, des bots, des power-ups et des manches.

## Contenu

| Dossier | Rôle |
| --- | --- |
| `BoomerangFuMultiplayer/` | Le plugin (C#, net48, HarmonyX). Architecture et historique des tests : `SETUP.md`. |
| `BoomerangFuMultiplayer.Patcher/` | Patcher BepInEx exécuté avant le jeu : il présente le jeu à Steam comme Spacewar (AppID 480). |
| `BoomerangFuMultiplayer/docs/` | Conception et plan d'implémentation des serveurs dédiés (mis de côté). |

Les versions prêtes à installer (zip avec BepInEx) sont dans les **Releases**.

## Compiler

Le dépôt doit être cloné dans le dossier du jeu, sous le nom `ModSource` : les projets référencent `..\..\Boomerang Fu_Data\Managed\` et `..\..\BepInEx\core\`. BepInEx 5.4.22 doit déjà être installé dans le jeu.

```
cd "<dossier du jeu>\ModSource\BoomerangFuMultiplayer"
dotnet build -c Release
cd ..\BoomerangFuMultiplayer.Patcher
dotnet build -c Release
```

Les DLL sortent directement dans `BepInEx\plugins\BoomerangFuMultiplayer\` et `BepInEx\patchers\BoomerangFuMultiplayer\`.

## Options de test

Le plugin lit quelques arguments de lancement pour les tests automatiques. Exemple : `-bfump-host-steam -bfump-autoplay -bfump-bots 2 -bfump-bot-difficulty impossible -bfump-wait-players 2` pour l'hôte, `+bfump_join <SteamID64 de l'hôte> -bfump-autoplay` pour un client. Liste complète dans `SETUP.md`.
