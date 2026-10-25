# Python package installation security note

The October 2026 eMudhra/emSign certificate incident affects Python package
installation tools that still trust the compromised certificate chain. The
Python packaging security notice recommends `pip` 26.2.2 or later and
`certifi` 2026.10.19 or later. Older `pip` versions include their own CA bundle,
so updating the operating system certificate store alone does not fix `pip`.
See the [Python packaging notice](https://discuss.python.org/t/information-for-python-users-regarding-emudhra-emsign-ca-certificate-security-incident/109576)
and the [certifi advisory](https://github.com/advisories/GHSA-6q48-6c7w-9gw2).

Screen Stocks Bridge's Python client has no third-party runtime dependencies
and connects only to the local game plugin. This notice applies to the tools
used to install or update Python packages, including this client.

## Update an existing Python environment

If the environment's `pip` is older than 26.2.2, bootstrap the fixed version
from its hash-pinned wheel before using the old installer to download anything
else. Run this separately for each Python environment or virtual environment
you use:

```powershell
python -m pip --disable-pip-version-check install --no-index --no-deps --upgrade "pip @ https://files.pythonhosted.org/packages/3f/06/3e4420302363fbb71be3c7f35ce042f54bfca330e3e66b3a2e3c4cca579f/pip-26.2.2-py3-none-any.whl#sha256=fd7f4dfa8190e014013b9f08f3e973f4e4c26ffd179a204895533a7254813a70"
python -m pip --version
python -m pip install --upgrade "pip>=26.2.2" "certifi>=2026.10.19"
```

After that, install or update the bridge normally:

```powershell
python -m pip install --upgrade screenstocks-bridge
```

The first command verifies the downloaded pip wheel against the SHA-256 hash
before installing it. Check that `python` points to the same interpreter whose
environment you intend to update. New virtual environments may start with an
older bundled pip, so apply this bootstrap in each one before installing
packages. Consult the Python packaging notice for updated versions or
instructions if its recommendation changes.
