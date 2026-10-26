"""Builds the built-in recommendations: one file per country, read by every TV (no key on the TV).

Run by .github/workflows/recs.yml every day, with the TMDB key kept as the TMDB_KEY secret: the key never leaves GitHub.
For each country: what's trending this week and what's new on each streaming service the app knows (Netflix, Prime
Video, Disney+...), each title with the services it streams on there. The TV keeps the ones whose app it has.

Usage: TMDB_KEY=... python3 tools/make_recs.py out/recs     (writes out/recs/FR.json, BE.json...)
"""
import json
import os
import re
import sys
import time
import urllib.parse
import urllib.request
from concurrent.futures import ThreadPoolExecutor
from datetime import date, timedelta

API = "https://api.themoviedb.org/3"
KEY = os.environ.get("TMDB_KEY", "").strip()
ANIMATION = 16

# Service name on TMDB (part of the name, lowercase) → the app's key (Recs.SERVICES on the TV).
SERVICES = [("netflix", "netflix"), ("prime video", "prime"), ("disney", "disney"), ("hulu", "hulu"),
            ("paramount", "paramount"), ("peacock", "peacock"), ("globoplay", "globoplay"), ("max", "max"),
            ("apple tv", "appletv"), ("hotstar", "hotstar"), ("okko", "okko"), ("crunchyroll", "crunchyroll"),
            ("oneplay", "oneplay")]

# The countries (the installer's list) and the language of their titles and genres.
FR = "fr-FR"
REGIONS = {
    "FR": FR, "BE": FR, "CH": FR, "LU": FR, "MC": FR, "RE": FR, "GP": FR, "MQ": FR, "GF": FR, "YT": FR, "NC": FR,
    "PF": FR, "SN": FR, "CI": FR, "CM": FR, "MG": FR, "MU": FR, "MA": FR, "DZ": FR, "TN": FR, "CA": "fr-CA",
    "US": "en-US", "GB": "en-GB", "IE": "en-GB", "AU": "en-AU", "NZ": "en-US", "ZA": "en-US", "NG": "en-US",
    "KE": "en-US", "JM": "en-US", "SG": "en-US", "MY": "en-US", "PH": "en-US", "IN": "hi-IN", "IS": "en-US",
    "DE": "de-DE", "AT": "de-DE", "ES": "es-ES", "PT": "pt-PT", "BR": "pt-BR", "IT": "it-IT", "NL": "nl-NL",
    "DK": "da-DK", "SE": "sv-SE", "NO": "nb-NO", "FI": "fi-FI", "PL": "pl-PL", "CZ": "cs-CZ", "SK": "sk-SK",
    "HU": "hu-HU", "RO": "ro-RO", "BG": "bg-BG", "GR": "el-GR", "HR": "hr-HR", "SI": "sl-SI", "RS": "sr-RS",
    "TR": "tr-TR", "UA": "uk-UA", "RU": "ru-RU", "EE": "et-EE", "LV": "lv-LV", "LT": "lt-LT", "IL": "he-IL",
    "SA": "ar-SA", "AE": "ar-AE", "QA": "ar-SA", "EG": "ar-EG", "LB": "ar-SA", "JP": "ja-JP", "KR": "ko-KR",
    "CN": "zh-CN", "HK": "zh-HK", "TW": "zh-TW", "TH": "th-TH", "ID": "id-ID", "VN": "vi-VN",
    "MX": "es-MX", "AR": "es-MX", "CL": "es-MX", "CO": "es-MX", "PE": "es-MX", "VE": "es-MX", "EC": "es-MX",
    "UY": "es-MX", "BO": "es-MX", "PY": "es-MX", "CR": "es-MX", "PA": "es-MX", "GT": "es-MX", "DO": "es-MX",
}
PER_SERVICE = 12   # new movies and new shows per service (and per country)
TRENDING = 20      # trending movies and shows looked at (this week)


def get(path, **params):
    """One TMDB request (v3 key or v4 read token), retried a few times when TMDB asks to slow down."""
    bearer = KEY.startswith("eyJ")
    if not bearer:
        params["api_key"] = KEY
    url = API + path + ("?" + urllib.parse.urlencode(params) if params else "")
    for attempt in range(5):
        req = urllib.request.Request(url, headers={"Accept": "application/json", "User-Agent": "FrontEndTV-recs"})
        if bearer:
            req.add_header("Authorization", "Bearer " + KEY)
        try:
            with urllib.request.urlopen(req, timeout=20) as r:
                return json.load(r)
        except urllib.error.HTTPError as e:
            if e.code == 429 or e.code >= 500:
                time.sleep(1 + attempt * 2)
                continue
            if e.code == 404:
                return {}
            raise
        except (urllib.error.URLError, TimeoutError):
            time.sleep(1 + attempt * 2)
    raise RuntimeError("TMDB doesn't answer: " + path)


def service(name, provider_id):
    """The app key for a TMDB service name, or None (same rules as the TV: no "… Channel", Apple TV+ is 350 only)."""
    n = name.lower()
    if " channel" in n:
        return None
    for part, key in SERVICES:
        if key == "appletv" and provider_id != 350:
            continue
        if re.search(r"\bmax\b", n) if key == "max" else part in n:
            return key
    return None


def best_backdrop(images, fallback):
    """No text on it (the TV writes its own), the closest to 1920 wide, then the best rated."""
    best, score = fallback, -1
    for b in (images or {}).get("backdrops", []):
        if not b.get("file_path"):
            continue
        w = b.get("width", 0)
        s = (1e7 if b.get("iso_639_1") is None else 0) + min(w, 1920) * 100 - max(0, w - 1920) + b.get("vote_average", 0)
        if s > score:
            best, score = b["file_path"], s
    return best


details = {}   # (kind, id) → what doesn't depend on the language: backdrop, runtime/seasons, services per country, English title


def detail(kind, tid):
    k = (kind, tid)
    if k not in details:
        o = get("/%s/%d" % (kind, tid), language="en-US", append_to_response="images,watch/providers",
                include_image_language="null,en")
        if not o:
            details[k] = None
            return None
        where = {}
        for region, offers in ((o.get("watch/providers") or {}).get("results") or {}).items():
            keys = []
            for f in offers.get("flatrate") or []:
                s = service(f.get("provider_name", ""), f.get("provider_id", 0))
                if s and s not in keys:
                    keys.append(s)
            if keys:
                where[region] = keys
        details[k] = {
            "backdrop": best_backdrop(o.get("images"), o.get("backdrop_path")),
            "length": o.get("runtime") if kind == "movie" else o.get("number_of_seasons"),
            "where": where,
            "title_en": o.get("title" if kind == "movie" else "name", ""),
            "original_language": o.get("original_language", ""),
            "animation": any(g.get("id") == ANIMATION for g in o.get("genres", [])),
        }
    return details[k]


def run(out_dir):
    if not KEY:
        print("::warning::Pas de secret TMDB_KEY : ajoute-le dans GitHub → Settings → Secrets → Actions.")
        return 0
    os.makedirs(out_dir, exist_ok=True)
    today = date.today()
    languages = sorted(set(REGIONS.values()))
    genres, trending = {}, {}
    for lang in languages:
        genres[lang] = {}
        for kind in ("movie", "tv"):
            for g in get("/genre/%s/list" % kind, language=lang).get("genres", []):
                genres[lang][g["id"]] = g["name"]
            trending[(lang, kind)] = get("/trending/%s/week" % kind, language=lang).get("results", [])[:TRENDING]

    pool = ThreadPoolExecutor(8)
    written = 0
    for region, lang in sorted(REGIONS.items()):
        ids = {}
        for kind in ("movie", "tv"):
            for p in get("/watch/providers/%s" % kind, watch_region=region).get("results", []):
                s = service(p.get("provider_name", ""), p.get("provider_id", 0))
                if s:
                    ids.setdefault(s, set()).add(p["provider_id"])
        if not ids:
            continue
        common = dict(language=lang, watch_region=region, with_watch_monetization_types="flatrate",
                      include_adult="false", sort_by="popularity.desc")
        lists = []   # (kind, items) in the order they're offered
        for kind in ("movie", "tv"):
            lists.append((kind, trending[(lang, kind)]))
        for s, pids in sorted(ids.items()):
            providers = "|".join(str(i) for i in sorted(pids))
            movies = get("/discover/movie", **common, with_watch_providers=providers, **{
                "vote_count.gte": 30, "primary_release_date.gte": str(today - timedelta(days=365)),
                "primary_release_date.lte": str(today)}).get("results", [])[:PER_SERVICE]
            shows = get("/discover/tv", **common, with_watch_providers=providers, **{
                "vote_count.gte": 20, "air_date.gte": str(today - timedelta(days=90)),
                "air_date.lte": str(today)}).get("results", [])[:PER_SERVICE]
            lists.append(("movie", movies))
            lists.append(("tv", shows))
        wanted = [(kind, o["id"]) for kind, items in lists for o in items]
        list(pool.map(lambda k: detail(*k), wanted))

        def entry(kind, o):
            d = details.get((kind, o["id"]))
            if not d or not d["backdrop"] or region not in d["where"]:
                return None
            field = "title" if kind == "movie" else "name"
            title = o.get(field) or d["title_en"]
            # Untranslated (the original title in another script or language): the English one, as the TV did
            if (title == o.get("original_" + field) and d["original_language"] not in (lang[:2], "en")
                    and d["title_en"]):
                title = d["title_en"]
            first = (o.get("genre_ids") or [None])[0]
            e = {"id": o["id"], "kind": kind, "title": title, "backdrop": d["backdrop"],
                 "genre": genres[lang].get(first, ""), "services": d["where"][region]}
            if d["length"]:
                e["length"] = d["length"]
            if d["animation"]:
                e["animation"] = True
            return e

        # Trending first (movies and shows alternating), then what's new, service by service, without repeats.
        titles, seen = [], set()
        rounds = [[entry(kind, o) for o in items] for kind, items in lists]
        for group in (rounds[:2], rounds[2:]):
            for i in range(max((len(r) for r in group), default=0)):
                for r in group:
                    e = r[i] if i < len(r) else None
                    if e and (e["kind"], e["id"]) not in seen:
                        seen.add((e["kind"], e["id"]))
                        titles.append(e)
        with open(os.path.join(out_dir, region + ".json"), "w", encoding="utf-8") as f:
            json.dump({"region": region, "language": lang, "at": int(time.time()), "titles": titles}, f,
                      ensure_ascii=False, separators=(",", ":"))
        written += 1
        print("%s: %d titres" % (region, len(titles)))
    print("%d pays, %d titres lus" % (written, len(details)))
    return 0 if written else 1


if __name__ == "__main__":
    sys.exit(run(sys.argv[1] if len(sys.argv) > 1 else "out/recs"))
