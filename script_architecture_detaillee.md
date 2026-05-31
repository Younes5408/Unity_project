# 🎙️ Script de Présentation : L'Architecture en Détail (Pour le Jury/Professeur)

Ce guide vous donne le script de parole exact et les arguments techniques pour expliquer la structure de votre projet. Les professeurs adorent les choix d'ingénierie justifiés ; ce script montre que chaque décision a été prise pour des raisons d'optimisation et de robustesse.

---

## 🛠️ Partie 1 : Introduction de l'Architecture (Devant le schéma)

**Ce que vous devez dire :**
> "Monsieur/Madame, pour bien comprendre comment fonctionne Archi-Agent VR, il faut comprendre le flux de données en temps réel qui relie notre client 3D Unity au serveur Python FastAPI et à la base de données MongoDB.
>
> Notre architecture est entièrement asynchrone et repose sur un principe de découplage fort. Le client Unity gère le rendu et la physique, le backend FastAPI gère le pipeline d'IA décisionnelle, et MongoDB assure la synchronisation et la persistance."

---

## 🔄 Partie 2 : Le Cycle de Vie d'une Commande Vocale (Le Flux de Données)

**Ce que vous devez dire :**
> "Voici le cycle de vie complet lorsque je dis, par exemple, *'Mets une table devant moi'* :
>
> 1. **Capture et Transmission** : Unity enregistre ma voix au format WAV en push-to-talk. Dès que je relâche le bouton, il envoie cet audio par requête HTTP `POST` au backend à la route `/api/chat/audio`. Avec ce fichier audio, Unity injecte des métadonnées cruciales dans les paramètres d'URL : les coordonnées de ma caméra `(player_x, player_z)` et mon angle de vue `player_angle`.
>
> 2. **Le Pipeline IA de Décision** :
>    - **Transcription (STT)** : Le backend envoie le WAV à Deepgram Nova-3. En moins de 300 ms, nous obtenons le texte français : *'Mets une table devant moi'*.
>    - **L'Agent LLM (Raisonnement)** : Nous utilisons le framework `agno` avec le modèle Llama 3.3 (70B) hébergé sur Groq. Grâce aux coordonnées du joueur transmises, l'agent calcule trigonométriquement un point à 3 mètres pile devant le joueur dans la direction de son regard. Ce point est stocké de manière thread-safe dans des `contextvars` Python.
>    - **Appel d'Outils (Tool Calling)** : L'agent comprend l'intention d'insérer un meuble. Il appelle la fonction Python `placer_objet(object_name="table")`.
>    - **Écriture en Base** : Cette fonction insère un document dans la collection `placements` de MongoDB avec un statut `pending` (en attente) contenant les coordonnées théoriques `(X, Y, Z)` calculées par le serveur.
>    - **Génération Vocale (TTS)** : L'agent génère sa réponse textuelle : *'J'ai placé la table devant vous'*. Edge-TTS convertit ce texte en fichier audio MP3.
>
> 3. **Le Retour et la Synthèse** : Le serveur répond à la requête initiale d'Unity en renvoyant le MP3 sous forme de chaîne encodée en Base64 ainsi que le texte de réponse. Unity décode le MP3, le joue, et fait passer l'avatar 3D en état `Talking`."

---

## 🛰️ Partie 3 : La Réalisation dans Unity (Le Polling et la Physique)

**Ce que vous devez dire :**
> "Pendant ce temps, comment Unity sait-il qu'il doit afficher la table ?
>
> C'est là qu'interviennent nos scripts de Polling. Unity interroge le backend toutes les 1,5 seconde sur des endpoints dédiés, comme `/api/placements/pending`. 
>
> Dès qu'Unity détecte notre document de table en statut `pending` :
> 1. Il charge dynamiquement le prefab 3D depuis son dossier `Resources`.
> 2. **Ajustement Physique (Raycasting)** : Il lance un rayon vers le bas (Raycast) depuis les coordonnées théoriques pour détecter le sol réel et ajuster la hauteur `Y` pour que la table ne flotte pas.
> 3. **Évitement de Collision (Smart Offset)** : Il exécute un test de collision par sphère. Si l'emplacement est déjà occupé par un autre meuble, Unity calcule automatiquement 5 positions alternatives autour du point visé et instancie l'objet sur le premier espace vide.
> 4. **Composants VR** : Unity ajoute dynamiquement un `Rigidbody` et un `XRGrabInteractable` pour que le joueur puisse ensuite attraper la table avec ses mains virtuelles.
> 5. **Confirmation (ACK)** : Une fois l'objet placé, Unity envoie un `POST` de confirmation au backend, ce qui fait passer le document dans MongoDB à l'état `delivered` (délivré). L'objet est définitivement enregistré et ne sera plus renvoyé par le polling."

---

## 🧠 Partie 4 : Les 3 Choix Techniques Majeurs à Mettre en Valeur

**Ce que vous devez dire (Les "Arguments en Or" pour le prof) :**

> "Pour finir, je souhaite mettre en avant trois choix techniques et d'ingénierie qui rendent notre projet particulièrement robuste :
>
> * **1. Pourquoi du Polling et pas du WebSocket ?** 
>   En VR autonome (Meta Quest), la connexion Wi-Fi subit fréquemment des micro-coupures de quelques secondes. Un WebSocket se déconnecterait et demanderait un code complexe de reconnexion et de gestion d'état. Avec notre système de Polling adaptatif avec acquittement (ACK) en base de données, si le Wi-Fi coupe pendant 3 secondes, Unity reprend les requêtes là où elles s'étaient arrêtées dès que le réseau revient, sans aucune perte de données ni crash.
>
> * **2. L'usage de contextvars Python pour la position spatiale :**
>   Les outils du LLM ne reçoivent comme paramètres que ce que le modèle décide d'écrire. Si on demande au LLM d'extraire la position du joueur pour la renvoyer aux outils, il fait des erreurs de calcul spatial. Nous avons résolu cela en utilisant des `contextvars` (variables de contexte globales par requête). Les outils accèdent directement à la position envoyée par Unity en tâche de fond, rendant le calcul spatial 100% déterministe et fiable.
>
> * **3. Les Cascades d'APIs (STT/LLM/TTS) :**
>   Comme nous utilisons des clés d'API gratuites sujettes à des quotas ou des pannes, notre backend implémente une cascade automatique à 3 niveaux. Si notre service de transcription ultra-rapide Deepgram échoue ou met plus de 8 secondes à répondre, le serveur bascule instantanément sur Groq Whisper, puis en dernier recours sur Gemini Flash-Lite. L'utilisateur ne subit aucun plantage."
