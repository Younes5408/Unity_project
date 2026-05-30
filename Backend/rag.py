"""
Lightweight RAG (Retrieval-Augmented Generation) for Archi-Agent VR.
Uses pure-Python BM25 over the knowledge_base/ directory.
No external ML dependencies (no transformers, FAISS, scikit-learn).
Index is built lazily on first query and cached in memory.
Supports French language with stopword filtering.
"""

import os
import re
from pathlib import Path
from typing import List, Dict, Optional
from collections import defaultdict
import math


# French stopwords (common words to ignore in indexing)
FRENCH_STOPWORDS = {
    'le', 'la', 'les', 'de', 'du', 'des', 'et', 'ou', 'un', 'une', 'des',
    'en', 'à', 'au', 'aux', 'par', 'pour', 'avec', 'sans', 'sous', 'sur',
    'dans', 'entre', 'vers', 'est', 'sont', 'avoir', 'être', 'avoir',
    'qu', 'que', 'qui', 'dont', 'où', 'quand', 'comment', 'pourquoi',
    'très', 'plus', 'moins', 'bon', 'mauvais', 'grand', 'petit',
    'ce', 'cet', 'cette', 'ces', 'il', 'elle', 'ils', 'elles',
    'je', 'tu', 'nous', 'vous', 'moi', 'toi', 'lui', 'me', 'te',
    'mon', 'ton', 'son', 'nos', 'vos', 'leur',
}


def normalize_text(text: str) -> str:
    """Normalize French text for indexing."""
    # Convert to lowercase
    text = text.lower()
    # Remove accents
    text = text.replace('é', 'e').replace('è', 'e').replace('ê', 'e').replace('ë', 'e')
    text = text.replace('à', 'a').replace('â', 'a').replace('ä', 'a')
    text = text.replace('ù', 'u').replace('û', 'u').replace('ü', 'u')
    text = text.replace('ï', 'i').replace('î', 'i')
    text = text.replace('ô', 'o').replace('ö', 'o').replace('ç', 'c')
    # Remove special characters, keep only alphanumeric and spaces
    text = re.sub(r'[^a-z0-9\s]', ' ', text)
    return text


def tokenize(text: str) -> List[str]:
    """Tokenize French text and remove stopwords."""
    # Normalize
    text = normalize_text(text)
    # Split into words
    tokens = text.split()
    # Filter stopwords and empty tokens
    tokens = [t for t in tokens if t and t not in FRENCH_STOPWORDS and len(t) > 1]
    return tokens


class BM25Index:
    """Pure-Python BM25 implementation for French text retrieval."""

    def __init__(self):
        """Initialize BM25 index."""
        self.documents = []  # List of {id, text, tokens}
        self.document_freq = defaultdict(int)  # How many docs contain each term
        self.term_freq = []  # List of {term: count} for each doc
        self.doc_lengths = []  # Token count for each doc
        self.avg_doc_length = 0

        # BM25 parameters
        self.k1 = 1.5  # Controls non-linear term frequency saturation point
        self.b = 0.75  # Controls to what degree document length normalizes tf values

        self.is_built = False

    def add_document(self, doc_id: str, text: str):
        """Add a document to be indexed."""
        tokens = tokenize(text)
        doc_entry = {
            'id': doc_id,
            'text': text,
            'tokens': tokens
        }
        self.documents.append(doc_entry)
        self.is_built = False

    def build(self):
        """Build the BM25 index from added documents."""
        if not self.documents:
            return

        self.document_freq = defaultdict(int)
        self.term_freq = []
        self.doc_lengths = []

        # First pass: count term frequencies and document frequencies
        for doc in self.documents:
            tokens = doc['tokens']
            self.doc_lengths.append(len(tokens))

            term_freq = defaultdict(int)
            for token in tokens:
                term_freq[token] += 1
                self.document_freq[token] += 1

            self.term_freq.append(dict(term_freq))

        # Calculate average document length
        total_length = sum(self.doc_lengths)
        num_docs = len(self.documents)
        self.avg_doc_length = total_length / num_docs if num_docs > 0 else 0

        self.is_built = True

    def search(self, query: str, top_k: int = 3) -> List[Dict]:
        """Search for documents matching the query."""
        if not self.is_built:
            self.build()

        if not self.documents:
            return []

        query_tokens = tokenize(query)
        if not query_tokens:
            return []

        scores = []
        num_docs = len(self.documents)

        # Calculate BM25 score for each document
        for doc_idx, doc in enumerate(self.documents):
            score = 0.0

            for token in query_tokens:
                if token not in self.document_freq:
                    continue

                # Document frequency
                df = self.document_freq[token]

                # Inverse document frequency with saturation
                idf = math.log((num_docs - df + 0.5) / (df + 0.5) + 1)

                # Term frequency in this document
                tf = self.term_freq[doc_idx].get(token, 0)

                # BM25 formula
                doc_len = self.doc_lengths[doc_idx]
                norm_tf = tf * (self.k1 + 1) / (
                    tf + self.k1 * (1 - self.b + self.b * (doc_len / self.avg_doc_length))
                )

                score += idf * norm_tf

            if score > 0:
                scores.append({
                    'id': doc['id'],
                    'text': doc['text'],
                    'score': score
                })

        # Sort by score and return top-k
        scores.sort(key=lambda x: x['score'], reverse=True)
        return scores[:top_k]


# Global RAG index
_rag_index = None


def _get_knowledge_base_path() -> Path:
    """Get the knowledge_base directory path."""
    kb_path = Path(__file__).parent / "knowledge_base"
    return kb_path


def _load_knowledge_base() -> BM25Index:
    """Load and index all markdown files from knowledge_base directory."""
    global _rag_index

    index = BM25Index()
    kb_path = _get_knowledge_base_path()

    if not kb_path.exists():
        print(f"[RAG] Knowledge base directory not found: {kb_path}")
        return index

    # Load all .md files from knowledge_base/
    md_files = list(kb_path.glob("*.md"))

    if not md_files:
        print(f"[RAG] No markdown files found in {kb_path}")
        return index

    for md_file in md_files:
        try:
            with open(md_file, 'r', encoding='utf-8') as f:
                content = f.read()
            doc_id = md_file.stem  # filename without extension
            index.add_document(doc_id, content)
            print(f"[RAG] Loaded: {md_file.name}")
        except Exception as e:
            print(f"[RAG] Error loading {md_file}: {e}")

    # Build the index
    if index.documents:
        index.build()
        print(f"[RAG] Index built with {len(index.documents)} documents")

    return index


def get_index() -> BM25Index:
    """Get the global RAG index (lazy loading)."""
    global _rag_index
    if _rag_index is None:
        _rag_index = _load_knowledge_base()
    return _rag_index


def retrieve(query: str, top_k: int = 3) -> str:
    """Retrieve relevant architectural context for a user query."""
    index = get_index()
    results = index.search(query, top_k=top_k)

    if not results:
        return ""

    # Format results as context
    context_parts = []
    for result in results:
        context_parts.append(f"📖 {result['id']}:\n{result['text'][:500]}...")

    context = "\n\n".join(context_parts)
    return context


def clear_cache():
    """Clear the global RAG cache (useful for testing)."""
    global _rag_index
    _rag_index = None
