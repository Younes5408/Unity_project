"""
Voice module for Archi-Agent VR.
Provides speech-to-text (STT) and text-to-speech (TTS) functionality.
STT cascade: Deepgram Nova-2 (primary, ~300ms) → Groq Whisper → Gemini
TTS cascade: edge-tts (primary, ~200-500ms, free, no key) → gTTS fallback
"""

import asyncio
import os
import base64
import re
import sys
from io import BytesIO
from typing import Optional


def _log(msg: str):
    """Print safely on Windows regardless of console encoding."""
    try:
        print(msg)
    except UnicodeEncodeError:
        print(msg.encode('ascii', 'replace').decode())

try:
    import requests as _requests
    _requests_available = True
except ImportError:
    _requests = None
    _requests_available = False

try:
    from groq import Groq as GroqClient
except ImportError:
    GroqClient = None

try:
    import edge_tts as _edge_tts
except ImportError:
    _edge_tts = None

try:
    from gtts import gTTS
except ImportError:
    gTTS = None

try:
    from google import genai
    from google.genai import types as genai_types
except ImportError:
    genai = None
    genai_types = None


# Get API keys from environment
# Note: project convention reuses LLM_API_KEY for the Groq key (see config.py).
DEEPGRAM_API_KEY = os.getenv("DEEPGRAM_API_KEY")
GROQ_API_KEY = os.getenv("GROQ_API_KEY") or os.getenv("LLM_API_KEY")
GEMINI_API_KEY = os.getenv("GEMINI_API_KEY")
GEMINI_STT_MODEL = "gemini-2.5-flash-lite"

# TTS provider settings — edge-tts is default (fast, free, no API key needed).
# Set TTS_PROVIDER=gtts in .env to force gTTS.
TTS_PROVIDER = os.getenv("TTS_PROVIDER", "edge-tts")
EDGE_TTS_VOICE = os.getenv("EDGE_TTS_VOICE", "fr-FR-DeniseNeural")


# Whisper has a well-known failure mode where silence/noise produces these canned phrases
# (it was trained on YouTube subtitle data). Discard them so the LLM doesn't act on them.
_SILENCE_HALLUCINATIONS = (
    "sous-titres réalisés par la communauté d'amara.org",
    "merci d'avoir regardé",
    "merci d'avoir regardé cette vidéo",
    "merci d'avoir regardé la vidéo",
    "abonnez-vous",
    "n'oubliez pas de vous abonner",
    "thanks for watching",
    "thank you for watching",
    "please subscribe",
    "♪",
    "[music]",
    "[musique]",
)

# Vocabulary hint biases Whisper toward the words the agent actually understands,
# preventing "chaise" → "chez", "ustensiles" → "hostile", "téléporte" → "tellement", etc.
_WHISPER_PROMPT = (
    "Commandes architecture VR en français. Vocabulaire: "
    "maison, route, chaise, table, lampadaire, barrière, sol, tuiles, "
    "cuisine, ustensiles, salon, chambre, salle de bain, couloir, "
    "tableau de bord, dashboard, analytics, statistiques, ouvre, ferme, "
    "mets, place, ajoute, construis, supprime, va à, téléporte-moi, "
    "à droite, à gauche, devant, derrière. "
    "Exemples: 'mets une chaise', 'ajoute une table', 'construis une maison de 100m carrés', "
    "'téléporte-moi à la maison 2', 'ouvre le tableau de bord'."
)


def _is_silence_hallucination(text: str) -> bool:
    if not text:
        return True
    t = text.strip().lower()
    if len(t) < 2:
        return True
    return any(phrase in t for phrase in _SILENCE_HALLUCINATIONS)


def speech_to_text(audio: "bytes | str", filename: str = "recording.wav") -> Optional[str]:
    """
    Convert speech audio to text.
    STT cascade:
      1. Deepgram Nova-2  — ~300ms, 200h/month free, low hallucination rate
      2. Groq Whisper     — high quality but free-tier queues → cold 5-20s
      3. Google Gemini    — last-resort fallback

    Args:
        audio: Either raw audio bytes (preferred) or a file path.
        filename: Name hint for MIME inference (Groq only).

    Returns:
        Transcribed text or None if all providers fail.
    """

    # Normalise input to bytes
    if isinstance(audio, (bytes, bytearray)):
        audio_bytes = bytes(audio)
    else:
        with open(audio, "rb") as f:
            audio_bytes = f.read()
        filename = audio

    # ── Tier 1: Deepgram Nova-2 ──────────────────────────────────────────────
    # Fastest free STT: consistent ~300-500ms p50. 200h/month on free plan.
    # Uses the REST API directly — stable across all SDK versions.
    #
    # Why nova-2 (not nova-3): nova-3 is English-first; its French model is
    # newer and noticeably less accurate than nova-2 for natural conversational
    # FR. We previously found that nova-2 with smart_format=true dropped short
    # (<2s) utterances. Fix: keep nova-2 but disable smart_format AND punctuate
    # to preserve every short fragment.
    if DEEPGRAM_API_KEY and _requests_available:
        try:
            resp = _requests.post(
                "https://api.deepgram.com/v1/listen",
                headers={
                    "Authorization": f"Token {DEEPGRAM_API_KEY}",
                    "Content-Type": "audio/wav",
                },
                params={
                    "model": "nova-2",
                    "language": "fr",
                    "punctuate": "true",
                    # smart_format intentionally OFF — it drops short utterances
                },
                data=audio_bytes,
                timeout=8,
            )
            resp.raise_for_status()
            text = resp.json()["results"]["channels"][0]["alternatives"][0]["transcript"]
            if text and _is_silence_hallucination(text):
                _log(f"[STT] Deepgram silence filtered: {text[:80]}")
                return None
            if text and text.strip():
                _log(f"[STT] Deepgram OK: {text[:60]}...")
                return text
            _log("[STT] Deepgram returned empty — falling back")
        except Exception as e:
            _log(f"[STT] Deepgram failed: {e}")

    # ── Tier 2: Groq Whisper ─────────────────────────────────────────────────
    # Excellent quality, but free tier has 18k audio-second/hour limit and
    # occasionally queues requests (causing 5-20s cold latency).
    if GROQ_API_KEY and GroqClient:
        try:
            client = GroqClient(api_key=GROQ_API_KEY)
            transcript = client.audio.transcriptions.create(
                file=(filename, BytesIO(audio_bytes), "audio/wav"),
                model="whisper-large-v3",
                language="fr",
                temperature=0.0,
                prompt=_WHISPER_PROMPT,
            )
            text = transcript.text
            if text and _is_silence_hallucination(text):
                _log(f"[STT] Groq silence filtered: {text[:80]}")
                return None
            _log(f"[STT] Groq OK: {text[:60]}...")
            return text
        except Exception as e:
            _log(f"[STT] Groq failed: {e}")

    # ── Tier 3: Gemini ───────────────────────────────────────────────────────
    if GEMINI_API_KEY and genai and genai_types:
        try:
            client = genai.Client(api_key=GEMINI_API_KEY)
            response = client.models.generate_content(
                model=GEMINI_STT_MODEL,
                contents=[
                    "Please transcribe this audio into French text. Return only the transcription.",
                    genai_types.Part.from_bytes(data=audio_bytes, mime_type="audio/wav"),
                ],
            )
            text = response.text
            _log(f"[STT] Gemini OK: {text[:60]}...")
            return text
        except Exception as e:
            _log(f"[STT] Gemini failed: {e}")

    _log("[STT] All providers failed")
    return None


def clean_text_for_tts(text: str) -> str:
    """
    Clean text before TTS processing.
    Remove JSON, code blocks, markdown syntax.
    """
    # Remove JSON code blocks
    text = re.sub(r'```json\s*[\s\S]*?```', '', text)
    # Remove markdown code blocks
    text = re.sub(r'```[\s\S]*?```', '', text)
    # Remove markdown links
    text = re.sub(r'\[([^\]]+)\]\([^\)]+\)', r'\1', text)
    # Remove markdown bold/italic
    text = re.sub(r'\*\*([^\*]+)\*\*', r'\1', text)
    text = re.sub(r'\*([^\*]+)\*', r'\1', text)
    text = re.sub(r'__([^_]+)__', r'\1', text)
    text = re.sub(r'_([^_]+)_', r'\1', text)
    # Remove HTML
    text = re.sub(r'<[^>]+>', '', text)
    # Remove extra whitespace
    text = re.sub(r'\s+', ' ', text).strip()
    # Keep reasonable length for TTS (max ~500 chars)
    if len(text) > 500:
        text = text[:497] + "..."

    return text


async def text_to_speech_base64(text: str) -> Optional[str]:
    """
    Convert text to speech and return as base64-encoded audio.
    Primary: edge-tts (~200-500ms, free, no API key).
    Fallback: gTTS.

    Args:
        text: Text to convert to speech

    Returns:
        Base64-encoded MP3 audio or None if TTS fails
    """

    text = clean_text_for_tts(text)
    if not text:
        return None

    # edge-tts — async, fast, no API key
    if TTS_PROVIDER != "gtts" and _edge_tts:
        try:
            communicate = _edge_tts.Communicate(text, EDGE_TTS_VOICE)
            chunks = []
            async for chunk in communicate.stream():
                if chunk["type"] == "audio":
                    chunks.append(chunk["data"])
            if chunks:
                audio_base64 = base64.b64encode(b"".join(chunks)).decode("utf-8")
                _log(f"[TTS] edge-tts OK: {len(audio_base64)} bytes")
                return audio_base64
        except Exception as e:
            _log(f"[TTS] edge-tts failed, falling back to gTTS: {e}")

    # gTTS fallback (sync — run in thread to avoid blocking event loop)
    if gTTS:
        try:
            def _gtts_sync():
                tts = gTTS(text=text, lang="fr", slow=False)
                buf = BytesIO()
                tts.write_to_fp(buf)
                buf.seek(0)
                return base64.b64encode(buf.read()).decode("utf-8")

            audio_base64 = await asyncio.to_thread(_gtts_sync)
            _log(f"[TTS] gTTS OK: {len(audio_base64)} bytes")
            return audio_base64
        except Exception as e:
            _log(f"[TTS] gTTS failed: {e}")

    _log("[TTS] all providers failed")
    return None


def text_to_speech_file(text: str, output_path: str) -> bool:
    """
    Convert text to speech and save as file.

    Args:
        text: Text to convert
        output_path: Where to save the MP3 file

    Returns:
        True if successful, False otherwise
    """

    # Clean text
    text = clean_text_for_tts(text)

    if not text:
        return False

    try:
        tts = gTTS(text=text, lang='fr', slow=False)
        tts.save(output_path)
        _log(f"[TTS] Saved to {output_path}")
        return True
    except Exception as e:
        _log(f"[TTS] Save failed: {e}")
        return False
