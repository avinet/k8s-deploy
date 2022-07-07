# @avinet/k8s-deploy

This is a deployment tool for Kubernetes clusters that supports simple variable
substitution in the style of Github Actions (`${{ scope.MY_VARIABLE }}`).

## Why should I use this?

If you're sick of writing YAML paths for your kustomization templates and don't
want to rely on a different tool with its own DSL, this is for you.

## YAML is a superset of JSON. Does this tool support JSON manifests?

Yes, to the extent SharpYaml does. It outputs intermediate manifests as YAML
regardless.

## How do I use this?

The tool is a CLI that can be used on its own. See k8s-deploy --help for
details.

The tool is also provided as a docker based Github Action.

With secrets from environment.

```yaml
jobs:
  deploy:
    name: Deploy to Azure Kubernetes (dev)
    runs-on: ubuntu-latest
    steps:
      - name: Checkout
        uses: actions/checkout@v2

      # Login to your cluster

      - uses: avinet/k8s-deploy@v1.24.0
        with:
          command: update ## or init
          manifests: ./manifests
          values: ./cluster.toml
          secretsFromEnv: SECRETS_TOML
          env:
            SECRETS_TOML: ${{ secrets.D_DEVDT_AKS_SECRETS_TOML }}
```

## Prerequisites

The tool relies on `kubectl`. It automatically downloads the appropriate version
if not already in path. If in path, it must match the version this tool is built
against (currently v1.24.0).

The tool checks that the cluster version is not newer and not older than two
minor versions behind `k8s-deploy`.

The tool assumes the kube config context is already configured.

The tool expects a structure in the manifests directory. **YAML files at the
root are not processed.**

## Manifests

The manifests must be structured as follows:

```
| ./manifests
|--> @import
|----> elastic-crd
|--> @init
|----> azure
|------> storage.yaml
|----> gcs
|------> storage.yaml
|----> resource.yaml
|--> elastic
|----> @secrets
|------> elastic-secret.yaml
|----> elastic.yaml
```

### `@import`

All files (not YAML) under `@import` must contain a path/URL to a manifest.
These will be applied directly to the cluster, if not already up to date (stored
as ConfigMaps under the `k8s-deploy-auto` namespace).

Imports are always run first.

Imports do not support variable substitution.

### `@init`

All YAML files under `@init` will only be run when `init` command is set. Use
with care.

Init manifests are run before other sub-directories are processed (but after
imports).

### Sub directories

Each sub-directory contains a group of yaml files. Sub-directories are processed
in alphabetical order.

### Variants

Each sub-directory (including `@init`) may contain folders denoting variant
names. Variant used is set in the values TOML file on the path
`cluster.variant`. Intended usage is to create resources needed per cluster
type, for example storage classes for AKS vs GCS.

Only one variant can be used at a time.

Variant files are processed before other files on the sub-directory level.

Variant files do not replace files on the sub-directory level.

### `@secrets`

The folder `@secrets` under each sub-directory (including `@init`) contains any
resources that may refer to values in the secrets TOML file. This is done to
ensure secrets are not accidentally set outside secret storage.

## TOML files

The `values.toml` file must contain all variables referenced with the variable
substitution syntax: `${{ some.variable.name }}`.

Note that keys containing `.` are not supported (i.e. `value["some.key"]`).

Example:

```toml
deployment = "my-application-namespace"

[cluster]
variant = "azure"
context = "some-kubernetes-context"

[some]
variable.name = "test"
```

The `deployment` key denotes the namespace checked to determine whether the
cluster has been initialized or not. The namespace is created automatically.

The `cluster.variant` key denotes the variant used when variant manifests are
available.

The `cluster.context` key denotes the (kube config) context used when deploying.
