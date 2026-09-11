# Translations

`data/lang/<code>.json` is what ships. Each is `{"strings": {"English": "Translated"}}` --
the English literal in the code is the key, looked up when the string is drawn, and a key that
is missing shows in English. Keep the `~y~` colour codes and the leading/trailing spaces
exactly: the mod glues fragments together on them.

To edit a language, edit the json. To regenerate all of them from the authoring dictionaries
here (`tr_*.py`), run `python make_langs.py` from this folder -- it refuses a colour code that
went missing, edge whitespace that changed, a key written twice, and reports coverage against
`uikeys.json`, which `uikeys.py` rebuilds from the source.

`usheap.py` reads a .NET assembly's user-string heap; `align3.py` pairs two heaps (an English
build and a translated one) -- how the Portuguese was recovered from a community dll.
