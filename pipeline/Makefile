# sam build runs build-NasbaApi (template.yaml sets BuildMethod: makefile).
# The package is the four files the API imports and requirements.txt, built
# for Lambda's Linux on arm64 rather than for this machine.

PIP ?= $(abspath .venv/bin/pip)
API_FILES = api.py database.py metrics.py sheetcsv.py

build-NasbaApi:
	cp $(API_FILES) "$(ARTIFACTS_DIR)/"
	$(PIP) install --quiet --disable-pip-version-check --upgrade -r requirements.txt \
		--target "$(ARTIFACTS_DIR)" --platform manylinux2014_aarch64 \
		--implementation cp --python-version 3.13 --only-binary=:all:
