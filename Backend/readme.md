# Archi-Agent VR - Installation rapide

Ce projet combine :

- un backend Python/FastAPI qui génère le plan architectural,
- MongoDB pour stocker les pièces,
- Unity pour afficher la maison en 3D,
- un contrôleur FPS pour se déplacer dans la scène.

Si tu veux le refaire sur un autre ordinateur, suis l'ordre ci-dessous.

## 1. Prérequis

Installe d'abord :

- Unity 6 LTS avec le module Windows Build Support si besoin,
- Python 3.10+,
- Docker Desktop,
- Git si tu veux récupérer le projet depuis un dépôt.

Vérifie aussi que le projet Unity utilise le nouveau Input System ou `Both` dans les paramètres.

## 2. Récupérer le projet

Copie le dossier du projet sur le nouvel ordinateur ou clone le dépôt.

```bash
git clone <URL_DU_DEPOT>
cd unity
```

Si tu as copié le projet manuellement, ouvre simplement le dossier racine du projet dans Unity Hub.

## 3. Lancer MongoDB

Le projet utilise le fichier `docker-compose.yml` pour démarrer MongoDB et Mongo Express.

Depuis la racine du projet :

```bash
docker compose up -d
```

Services lancés :

- MongoDB sur `mongodb://admin:admin123@localhost:27017/admin?authSource=admin`
- Mongo Express sur `http://localhost:8081`

Identifiants Mongo Express :

- utilisateur : `admin`
- mot de passe : `pass`

## 4. Préparer le backend Python

Crée un environnement virtuel puis installe les dépendances.

```bash
python -m venv .venv
.\.venv\Scripts\activate
pip install -r requirements.txt
```

Crée ensuite un fichier `.env` à la racine du projet avec ta clé API LLM :

```env
LLM_API_KEY=ta_cle_api_ici
```

Le fichier `config.py` lit cette variable avec `LLM_API_KEY`.

## 5. Lancer le backend FastAPI

Le backend expose l’API utilisée par Unity.

```bash
python api.py
```

Si tout est bon, le serveur écoute sur :

- `http://127.0.0.1:8000`

Tu peux aussi lancer le mode console du générateur de pièces :

```bash
python main.py
```

# Archi-Agent VR - Installation rapide

Ce projet combine :

- un backend Python/FastAPI qui génère le plan architectural,
- MongoDB pour stocker les pièces,
- Unity pour afficher la maison en 3D,
- un contrôleur FPS pour se déplacer dans la scène.

Si tu veux le refaire sur un autre ordinateur, suis l'ordre ci-dessous.

## 1. Prérequis

Installe d'abord :

- Unity 6 LTS avec le module Windows Build Support si besoin,
- Python 3.10+,
- Docker Desktop,
- Git si tu veux récupérer le projet depuis un dépôt.

Vérifie aussi que le projet Unity utilise le nouveau Input System ou `Both` dans les paramètres.

## 2. Récupérer le projet

Copie le dossier du projet sur le nouvel ordinateur ou clone le dépôt.

```bash
git clone <URL_DU_DEPOT>
cd unity
```

Si tu as copié le projet manuellement, ouvre simplement le dossier racine du projet dans Unity Hub.

## 3. Lancer MongoDB

Le projet utilise le fichier `docker-compose.yml` pour démarrer MongoDB et Mongo Express.

Depuis la racine du projet :

```bash
docker compose up -d
```

Services lancés :

- MongoDB sur `mongodb://admin:admin123@localhost:27017/admin?authSource=admin`
- Mongo Express sur `http://localhost:8081`

Identifiants Mongo Express :

- utilisateur : `admin`
- mot de passe : `pass`

## 4. Préparer le backend Python

Crée un environnement virtuel puis installe les dépendances.

```bash
python -m venv .venv
.\.venv\Scripts\activate
pip install -r requirements.txt
```

Crée ensuite un fichier `.env` à la racine du projet avec ta clé API LLM :

```env
LLM_API_KEY=ta_cle_api_ici
```

Le fichier `config.py` lit cette variable avec `LLM_API_KEY`.

## 5. Lancer le backend FastAPI

Le backend expose l’API utilisée par Unity.

```bash
python api.py
```

Si tout est bon, le serveur écoute sur :

- `http://127.0.0.1:8000`

Tu peux aussi lancer le mode console du générateur de pièces :

```bash
python main.py
```

## 6. Ouvrir le projet dans Unity

Ouvre le dossier racine dans Unity Hub, puis attends l’import des scripts.

Dans Unity, vérifie le projet avec ces réglages :

- `Edit > Project Settings > Player > Active Input Handling` : mets `Input System Package (New)` ou `Both`.
- Si tu laisses `Input System Package (New)`, les scripts FPS fournis sont déjà compatibles.

## 7. Créer la scène Unity

La hiérarchie minimale doit ressembler à ceci :

- `Directional Light`
- `Global Volume`
- `ManagerIA` ou `Plan`
- `Player`
  - `Main Camera`

Tu peux créer un GameObject vide `Plan` ou `ManagerIA` pour gérer le layout.

### A. Objets à créer

1. Crée un `Empty Object` nommé `Player`.
2. Ajoute le composant `CharacterController` sur `Player`.
3. Ajoute le script `FirstPersonController.cs` sur `Player`.
4. Fais de `Main Camera` un enfant de `Player`.
5. Dans l’inspecteur du script `FirstPersonController`, assigne `Main Camera` dans le champ `Player Camera`.
6. Crée un `Empty Object` nommé `Plan`.
7. Ajoute le script `LayoutReceiver.cs` sur `Plan`.
8. Ajoute le script `CreateDefaultGround.cs` sur `Plan` si tu veux un sol automatique de secours.

### B. Réglages du `Player`

Sur `Player`, garde ces valeurs de base :

- `CharacterController Radius` : `0.5`
- `CharacterController Height` : `2`
- `CharacterController Step Offset` : `0.3`

Dans le script `FirstPersonController` :

- `Walk Speed` : `5`
- `Mouse Sensitivity` : `2` par défaut, mais tu peux augmenter si tu veux une caméra plus rapide.

### C. Réglages de `Main Camera`

- Elle doit être enfant de `Player`.
- Sa position locale peut rester proche de `(0, 1.6, 0)` si tu veux une vue naturelle.
- Le script la pilote verticalement, donc ne mets pas de rotation manuelle au départ.

## 8. Créer le sol

Tu as 2 options.

### Option simple

Crée un `Plane` :

- `GameObject > 3D Object > Plane`
- Position : `X 0`, `Y 0`, `Z 0`
- Scale : `10, 1, 10`

Laisse son `Collider` actif.

### Option automatique

Utilise `CreateDefaultGround.cs` sur un `Empty Object`.
Ce script crée un grand plane si aucun sol n’est trouvé.

## 9. Démarrer la scène dans le bon ordre

Ordre conseillé :

1. Lance Docker Desktop et `docker compose up -d`.
2. Lance `python api.py`.
3. Ouvre Unity et clique sur Play.
4. Si besoin, lance `python main.py` pour générer la maison depuis la console.

## 10. Comment ça se comporte dans Unity

- `LayoutReceiver.cs` interroge `http://127.0.0.1:8000/api/layout/maison_001` toutes les 2 secondes.
- Il reconstruit les pièces dans la scène.
- `FirstPersonController.cs` gère le déplacement FPS avec collision via `CharacterController.Move()`.
- `CreateDefaultGround.cs` évite de tomber dans le vide si aucun sol n’existe.

## 11. Problèmes fréquents

### Je ne vois rien dans la scène

- Vérifie que `api.py` tourne.
- Vérifie que MongoDB tourne avec `docker compose up -d`.
- Vérifie que la maison a été initialisée avec `python main.py` ou avec l’agent.

### Le joueur tombe dans le vide

- Vérifie qu’il existe un `Plane` avec `Collider`.
- Vérifie que `CreateDefaultGround.cs` est bien sur un objet actif.
- Vérifie que `Player` a bien un `CharacterController`.

### La caméra ne bouge pas

- Vérifie que `Main Camera` est enfant du `Player`.
- Vérifie que le champ `Player Camera` du script est bien rempli.

### Erreur Input System

- Va dans `Edit > Project Settings > Player`.
- Mets `Active Input Handling` sur `Input System Package (New)` ou `Both`.

## 12. Fichiers importants

- `FirstPersonController.cs` : déplacement FPS et souris.
- `CreateDefaultGround.cs` : sol automatique de secours.
- `LayoutReceiver.cs` : récupération et rendu des pièces depuis l’API.
- `api.py` : serveur FastAPI.
- `main.py` : mode console pour tester l’agent.
- `docker-compose.yml` : MongoDB et Mongo Express.

## 13. Résumé express

Si tu veux juste le minimum pour faire marcher le projet vite :

1. `docker compose up -d`
2. `python -m venv .venv`
3. `.\.venv\Scripts\activate`
4. `pip install -r requirements.txt`
5. créer `.env` avec `LLM_API_KEY=...`
6. `python api.py`
7. dans Unity : `Player` + `CharacterController` + `FirstPersonController`, `Main Camera` enfant du `Player`
8. créer `Plan` avec `LayoutReceiver` et `CreateDefaultGround`
9. Play
