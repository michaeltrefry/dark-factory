# Compiled into the installed, self-contained source client after the sealed
# remote engine and GUI adapter. Never imported from a source archive.
import base64
import configparser
import sys

SOURCE_PROTOCOL = 'factory-gui-source-v1'
SOURCE_WORKFLOWS = {'.github/workflows/' + name for name in (
    'inference-adapter.yml', 'live-raw-acceptance.yml', 'rust-catalog-tests.yml',
    's47-procedural-ui-tour.yml')}


def source_plan(text):
    if len(text) > 150000:
        raise ValueError('source job plan exceeds bound')
    value = json.loads(base64.b64decode(text, validate=True))
    if not isinstance(value, dict) or set(value) != {'version', 'workflow', 'jobId', 'timeoutMinutes', 'submodules', 'env', 'steps', 'artifacts'} or type(value['version']) is not int or value['version'] != 1:
        raise ValueError('invalid source job schema')
    if value['workflow'] not in SOURCE_WORKFLOWS or not re.fullmatch('[A-Za-z0-9_-]{1,100}', value['jobId']):
        raise ValueError('source workflow not allowlisted')
    if type(value['timeoutMinutes']) is not int or not 1 <= value['timeoutMinutes'] <= 360 or value['submodules'] not in ('false', 'recursive'):
        raise ValueError('source job bounds')
    source_environment(value['env'], '/source', '/temp')
    if not isinstance(value['steps'], list) or not 1 <= len(value['steps']) <= 50 or not isinstance(value['artifacts'], list) or len(value['artifacts']) > 10:
        raise ValueError('source job step bounds')
    for step in value['steps']:
        if not isinstance(step, dict) or set(step) not in ({'name', 'id', 'run', 'condition', 'env', 'timeoutMinutes'}, {'name', 'id', 'run', 'condition', 'env', 'timeoutMinutes', 'shell'}):
            raise ValueError('invalid source step')
        if step.get('shell', 'implicit-bash') not in ('implicit-bash', 'bash'):
            raise ValueError('unsupported source shell')
        if not isinstance(step['run'], str) or not 0 < len(step['run']) <= 50000 or '\0' in step['run']:
            raise ValueError('invalid source script')
        if step['condition'] not in ('success()', 'always()', "env.LW_INFERENCE_REAL == '1'"):
            raise ValueError('source condition not supported')
        if type(step['timeoutMinutes']) is not int or not 1 <= step['timeoutMinutes'] <= value['timeoutMinutes']:
            raise ValueError('source step timeout')
        source_environment(step['env'], '/source', '/temp')
    for artifact in value['artifacts']:
        if not isinstance(artifact, dict) or set(artifact) != {'name', 'paths', 'condition', 'ifNoFilesFound', 'retentionDays'}:
            raise ValueError('invalid source artifact')
        if artifact['condition'] not in ('success()', 'always()') or artifact['ifNoFilesFound'] not in ('error', 'warn', 'ignore') or type(artifact['retentionDays']) is not int or not 1 <= artifact['retentionDays'] <= 90:
            raise ValueError('source artifact bounds')
        if not isinstance(artifact['paths'], list) or not 1 <= len(artifact['paths']) <= 20 or any(not isinstance(p, str) or len(p) > 1000 or '\0' in p for p in artifact['paths']):
            raise ValueError('invalid source artifact paths')
    return value


def source_environment(values, source, temporary):
    if not isinstance(values, dict) or len(values) > 64:
        raise ValueError('invalid source environment')
    result = {}
    for key, value in values.items():
        if (not re.fullmatch('[A-Z][A-Z0-9_]{0,99}', key) or key.startswith(('GITHUB_', 'ACTIONS_', 'RUNNER_', 'FACTORY_', 'DYLD_', 'LD_'))
                or key in ('HOME', 'PATH', 'TMPDIR', 'BASH_ENV', 'ENV', 'SHELLOPTS') or re.search('TOKEN|SECRET|PASSWORD|CREDENTIAL|PRIVATE_KEY', key)):
            raise ValueError('reserved source environment')
        if not isinstance(value, str) or len(value) > 4096 or '\0' in value:
            raise ValueError('invalid source environment value')
        expanded = value.replace('${source}', str(source)).replace('${temp}', str(temporary)).replace('${var:LW_REQUIRE_REAL}', os.environ.get('FACTORY_GUI_VAR_LW_REQUIRE_REAL', ''))
        if '${' in expanded:
            raise ValueError('unknown source environment placeholder')
        result[key] = expanded
    return result


def source_public_json(repo, sha):
    # Immutable public Git trees only; collector credentials are NEVER sent to a
    # source repository or its dependencies.
    if not re.fullmatch(r'[A-Za-z0-9_.-]+/[A-Za-z0-9_.-]+', repo) or not re.fullmatch('[0-9a-f]{40}', sha):
        raise ValueError('invalid source tree identity')
    url = artifact_url('https://api.' + 'github.com/repos/' + repo + '/git/trees/' + sha)
    connection = artifact_connection(url)
    try:
        connection.request('GET', url.path, headers={'User-Agent': 'dark-factory-source', 'Accept': 'application/vnd.github+json'})
        response = connection.getresponse()
        if response.status != 200:
            raise ValueError('public source tree unavailable')
        data = response.read(4 * 1024 * 1024 + 1)
        remaining()
    finally:
        connection.close()
    if len(data) > 4 * 1024 * 1024:
        raise ValueError('source tree too large')
    tree = json.loads(data)
    if tree.get('truncated') or not isinstance(tree.get('tree'), list):
        raise ValueError('source tree incomplete')
    return tree['tree']


def source_archive(repo, sha, destination, archive):
    if not re.fullmatch(r'[A-Za-z0-9_.-]+/[A-Za-z0-9_.-]+', repo) or not re.fullmatch('[0-9a-f]{40}', sha):
        raise ValueError('immutable source identity required')
    url = artifact_url('https://codeload.' + 'github.com/' + repo + '/zip/' + sha)
    connection = artifact_connection(url)
    try:
        connection.request('GET', url.path, headers={'Accept-Encoding': 'identity'})
        response = connection.getresponse()
        if response.status != 200:
            raise ValueError('public immutable source unavailable')
        size = 0
        with archive.open('xb') as output:
            while chunk := response.read(65536):
                remaining()
                size += len(chunk)
                if size > 100 * 1024 * 1024:
                    raise ValueError('source download exceeds bound')
                output.write(chunk)
    finally:
        connection.close()
    if destination.exists():
        destination.rmdir()  # only the checked empty gitlink placeholder
    unpack(archive, destination)
    if any(path.name.lower() == '.git' for path in destination.rglob('*')):
        raise ValueError('source archive contains git administration')


def source_git_metadata(repo, sha, destination, staging):
    """Build a fully self-contained sanitized object database for the checked-out
    source tree BEFORE handoff, using only the existing bounded smart-HTTP pinned
    public transport. The handed-off .git carries no remotes, credentials, config,
    hooks or alternates, and no git runs against source-owned configuration after
    handoff: the source principal sees only objects bound to the pinned commit.

    The pinned commit's own pack (depth 1: the commit, its tree and blobs) is
    fetched first for exact verification. CONNECTING HISTORY is then fetched
    EXPLICITLY (fetch_package_pack with a bounded deepen depth, never relying on
    a server's refusal of an unadvertised ref): depth-1 carries no parent
    objects, so without the explicit deepened pack `git describe --tags` cannot
    see a release tag on a reachable ancestor and `merge-base` cannot walk
    ancestry. Advertised refs are read once (bounded to 10,000); each candidate
    tag pack is fetched, VERIFIED with index-pack, and INSTALLED into the
    metadata object store (index-pack alone verifies a pack in place on a
    staging path — it does not install objects into the store). An annotated
    tag's tag object is fetched via its own advertised ref; its peeled commit
    decides reachability. A tag is kept only when its commit is an ancestor of
    the pinned one; anything else advertised is dropped. Byte, ref and timeout
    bounds all hold; no full clone and no all-tags fetch."""
    if not re.fullmatch(r'[A-Za-z0-9_.-]+/[A-Za-z0-9_.-]+', repo) or not re.fullmatch('[0-9a-f]{40}', sha):
        raise ValueError('immutable source identity required')
    url = 'https://github.' + 'com/' + repo + '.git'
    admin = destination / '.git'
    admin.mkdir()
    env = {'PATH': '/usr/bin:/bin:/usr/sbin:/sbin', 'HOME': str(staging),
           'GIT_CONFIG_NOSYSTEM': '1', 'GIT_CONFIG_GLOBAL': '/dev/null',
           'GIT_ALLOW_PROTOCOL': 'file', 'GIT_TERMINAL_PROMPT': '0'}
    # A real bare repository skeleton (HEAD, refs/, config) is required before
    # index-pack can install anything; a bare mkdir is not a repository.
    code, _ = command(['/usr/bin/git', 'init', '--bare', str(admin)], str(staging), env=env)
    if code:
        raise ValueError('source metadata repository could not be initialized')
    pack_store = admin / 'objects/pack'
    pack_store.mkdir(parents=True, exist_ok=True)

    def git(*args):
        return command(['/usr/bin/git', '--git-dir=' + str(admin), '-c', 'core.hooksPath=/dev/null',
                        '-c', 'gc.auto=0', *args], str(staging), env=env)

    def install(pack):
        """Verify one fetched pack with index-pack and INSTALL pack and index
        INTO the metadata object store. Nothing is deleted before it is
        installed; no staging copy remains."""
        try:
            code, indexed = git('index-pack', str(pack))
            if code:
                raise ValueError('source metadata pack did not verify')
            pack_hash = indexed.strip().splitlines()[-1] if indexed.strip() else ''
            if not re.fullmatch('[0-9a-f]{40}', pack_hash):
                raise ValueError('invalid source metadata pack hash')
        except Exception:
            pack.unlink(missing_ok=True)
            pack.with_suffix('.idx').unlink(missing_ok=True)
            raise
        pack.rename(pack_store / ('pack-' + pack_hash + '.pack'))
        pack.with_suffix('.idx').rename(pack_store / ('pack-' + pack_hash + '.idx'))

    def fetch(revision, label, depth):
        """One bounded shallow pack at the given depth, fetched and installed.
        depth 1 verifies the exact pinned objects; the deepened pack carries the
        connecting history. fetch_package_pack never falls back and never
        widens beyond the named revision."""
        pack = staging / ('git-metadata-' + label + '-' + revision[:16] + '-d%d.pack' % depth)
        fetch_package_pack(url, revision, pack, depth=depth)
        install(pack)

    def shallow_boundary():
        # The transport verifies packs but does not install Git's shallow sideband.
        # Reconstruct it from the installed commit graph: a known commit whose
        # parent object is missing is a shallow root. Clear the OLD boundary first,
        # or Git would refuse to walk newly installed connecting history.
        boundary = admin / 'shallow'
        boundary.unlink(missing_ok=True)
        code, rows = git('rev-list', '--parents', '--missing=allow-any', '--max-count=4097', sha)
        lines = [line.split() for line in rows.splitlines() if line.strip()]
        if code or not lines or len(lines) > 4096 or any(not all(re.fullmatch('[0-9a-f]{40}', value) for value in line) for line in lines):
            raise ValueError('source metadata history is unreadable or exceeds 4096 commits')
        known = {line[0] for line in lines}
        roots = sorted(line[0] for line in lines if any(parent not in known for parent in line[1:]))
        if roots:
            boundary.write_text(''.join(root + '\n' for root in roots))

    # Exact pinned objects first. Then explicitly deepen, replacing (not keeping)
    # the initial HEAD boundary with the actual remaining server-depth boundary.
    fetch(sha, 'head', 1)
    shallow_boundary()
    fetch(sha, 'history', 4096)
    shallow_boundary()
    for args in (['cat-file', '-e', sha + '^{commit}'], ['update-ref', 'HEAD', sha],
                 ['symbolic-ref', 'ORIG_HEAD', 'HEAD'], ['read-tree', 'HEAD']):
        code, _ = git(*args)
        if code:
            raise ValueError('pinned source commit is not verifiable')

    # Advertised refs, read once, bounded. An annotated tag advertises its tag
    # object under refs/tags/x and its peeled commit under refs/tags/x^{}.
    data, _ = public_resource(url.rstrip('/') + '/info/refs?service=git-upload-pack',
                              'application/x-git-upload-pack-advertisement')
    refs, peeled, offset = {}, {}, 0
    while offset + 4 <= len(data):
        header = data[offset:offset + 4]
        if not re.fullmatch(b'[0-9a-fA-F]{4}', header):
            raise ValueError('invalid Git reference advertisement')
        length = int(header, 16); offset += 4
        if length == 0:
            continue
        if length < 4 or offset + length - 4 > len(data):
            raise ValueError('truncated Git reference advertisement')
        line = data[offset:offset + length - 4].split(b'\0', 1)[0].strip()
        offset += length - 4
        match = re.fullmatch(b'([0-9a-f]{40}) ([^\r\n ]+)', line)
        if not match:
            continue
        name = match[2].decode('utf-8')
        if name.endswith('^{}'):
            peeled[name[:-3]] = match[1].decode('ascii')
        else:
            refs[name] = match[1].decode('ascii')
        if len(refs) + len(peeled) > 10000:
            raise ValueError('too many advertised Git references')

    tags = [name for name, value in refs.items() if value == sha and name.startswith('refs/tags/')]
    candidates = sorted(name for name in refs if name not in tags and name.startswith('refs/tags/'))
    if len(tags) + len(candidates) > 64:
        raise ValueError('too many tags on the pinned source history')

    for name in candidates:
        if len(tags) >= 64:
            raise ValueError('too many tags on the pinned source history')
        value = refs[name]
        # Check ancestry BEFORE any tag fetch: the peeled commit (or the
        # lightweight ref value) is already known from the single bounded
        # advertisement, and the head history pack already carries every
        # ancestor commit, so a known non-ancestor is skipped with NO network
        # round trip at all.
        target = peeled.get(name, value)
        ancestor, _ = git('merge-base', '--is-ancestor', target, sha)
        if ancestor != 0:
            continue  # another branch's tag: dropped before any fetch
        if name in peeled:
            # Annotated tag: its own tag OBJECT is not in any pack yet, so one
            # bounded depth-1 fetch (the tag object only) is needed.
            pack = staging / ('git-metadata-tag-' + value[:16] + '.pack')
            try:
                fetch_package_pack(url, value, pack, depth=1)
                install(pack)
            except Exception:
                pack.unlink(missing_ok=True)
                pack.with_suffix('.idx').unlink(missing_ok=True)
                continue  # an unavailable or unverifiable tag is skipped
        # A lightweight tag needs no extra pack: its commit is already in the
        # installed head history; only the ref is written below.
        tags.append(name)

    for name in tags:
        code, _ = git('update-ref', name, refs[name])
        if code:
            raise ValueError('pinned source tag is not verifiable')
    with open(admin / 'HEAD', 'w') as head:
        head.write(sha + '\n')  # detached HEAD: the exact pinned commit
    # A real work-tree git dir (bare = false) is required: git-submodule refuses
    # to run from a bare administration directory. read-tree materialized the
    # index above, so git-submodule reads each gitlink from the pinned tree.
    (admin / 'config').write_text('[core]\n\trepositoryformatversion = 0\n\tfilemode = true\n\tbare = false\n')
    for forbidden in ('objects/info/alternates',):
        if (admin / forbidden).exists():
            raise ValueError('source metadata administration refused')
    for emptyable in ('hooks', 'logs'):
        directory = admin / emptyable
        if directory.exists():
            import shutil
            shutil.rmtree(directory)
    for entry in admin.rglob('*'):
        entry.chmod(0o444 if entry.is_file() else 0o555)
    admin.chmod(0o555)


def source_submodules(repo, sha, root, staging, depth=0, count=None):
    count = count if count is not None else [0]
    if depth > 5:
        raise ValueError('submodule depth exceeded')
    modules = root / '.gitmodules'
    if not modules.is_file():
        return
    if modules.stat().st_size > 65536:
        raise ValueError('submodule declarations exceed bound')
    parser = configparser.ConfigParser(interpolation=None, strict=True)
    parser.read_string(modules.read_text())
    if len(parser.sections()) > 30 or parser.defaults():
        raise ValueError('submodule declaration count or defaults refused')
    entries = source_public_json(repo, sha)
    # Gitlink paths may be nested in tree objects; walk only the exact declared
    # path, never follow repository scripts or evaluate git configuration.
    def gitlink(parts, tree):
        found = [e for e in tree if e.get('path') == parts[0]]
        if len(found) != 1:
            raise ValueError('submodule tree entry unavailable')
        node = found[0]
        if len(parts) > 1:
            if node.get('type') != 'tree':
                raise ValueError('submodule parent not a tree')
            return gitlink(parts[1:], source_public_json(repo, node['sha']))
        if node.get('mode') != '160000' or node.get('type') != 'commit' or not re.fullmatch('[0-9a-f]{40}', node.get('sha', '')):
            raise ValueError('submodule pin unavailable')
        return node['sha']
    for section in parser.sections():
        if not section.startswith('submodule ') or set(parser[section]) != {'path', 'url'}:
            raise ValueError('unknown submodule configuration')
        relative = parser[section]['path']
        path = pathlib.PurePosixPath(relative)
        if len(relative) > 1000 or len(path.parts) > 20 or path.is_absolute() or any(p in ('..', '.git') for p in path.parts) or not path.parts:
            raise ValueError('submodule path refused')
        url = parser[section]['url']
        match = re.fullmatch(r'https://github[.]com/([A-Za-z0-9_.-]+/[A-Za-z0-9_.-]+?)(?:[.]git)?/?', url)
        if not match:
            raise ValueError('submodule must be a public GitHub repository')
        pin = gitlink(list(path.parts), entries)
        destination = root.joinpath(*path.parts)
        if destination.exists() and (not destination.is_dir() or any(destination.iterdir())):
            raise ValueError('submodule destination is not empty')
        count[0] += 1
        if count[0] > 30:
            raise ValueError('too many submodules')
        destination.mkdir(parents=True, exist_ok=True)
        source_archive(match[1], pin, destination, staging / ('submodule-' + str(count[0]) + '.zip'))
        source_submodules(match[1], pin, destination, staging, depth + 1, count)


# Read source-produced command files as that source principal, not as collector.
SOURCE_STATE_SCRIPT = r'''
import json, pathlib, sys
root=pathlib.Path(sys.argv[1]); result={}
for name in ('env','path'):
    path=root/name
    if path.is_symlink() or not path.is_file() or path.stat().st_size>65536: raise ValueError('invalid command file')
    result[name]=path.read_text()
print(json.dumps(result))
'''


def source_command_state(principal, temp):
    code, text = principal.run(['/usr/bin/python3', '-I', '-c', SOURCE_STATE_SCRIPT, str(temp)], principal.worktree)
    if code:
        raise ValueError('source command state unavailable')
    result = json.loads(text)
    env = {}
    lines = iter(result['env'].splitlines())
    for line in lines:
        if not line:
            continue
        if '<<' in line:
            key, delimiter = line.split('<<', 1)
            values = []
            for value in lines:
                if value == delimiter:
                    break
                values.append(value)
            else:
                raise ValueError('unterminated source command value')
            env[key] = '\n'.join(values)
        elif '=' in line:
            key, value = line.split('=', 1)
            env[key] = value
        else:
            raise ValueError('invalid source command state')
    # Dynamic repository variables remain source-only and cannot widen collector
    # or loader authority. PATH additions must stay within this fresh workspace.
    environment = source_environment(env, principal.worktree, temp)
    paths = result['path'].splitlines()
    if len(paths) > 50 or any(not pathlib.Path(p).is_absolute() or not pathlib.Path(p).resolve().is_relative_to(principal.root) for p in paths):
        raise ValueError('source PATH escapes workspace')
    return environment, paths


def source_export(source, patterns, destination, budget):
    # Walk/open relative to held no-follow directory descriptors. A source link
    # never makes the collector read another principal's credentials.
    import fnmatch
    matches = set()
    selectors = []
    for pattern in patterns:
        base = source.parent / 'source-temp' if pattern.startswith('${temp}/') else source
        expanded = pattern.removeprefix('${source}/').removeprefix('${temp}/')
        if pathlib.PurePosixPath(expanded).is_absolute() or '..' in pathlib.PurePosixPath(expanded).parts or expanded.startswith('!'):
            raise ValueError('artifact path outside source')
        selectors.append((base, expanded))
    visited = 0
    for base in {item[0] for item in selectors}:
        selected_dirs = []
        for directory, dirs, files in os.walk(base, followlinks=False):
            for name in list(dirs):
                if (pathlib.Path(directory) / name).is_symlink():
                    dirs.remove(name)
            for name in dirs + files:
                entry = pathlib.Path(directory) / name
                visited += 1
                if visited > 100000:
                    raise ValueError('source artifact inventory bound')
                relative = entry.relative_to(base).as_posix()
                selected = any(b == base and fnmatch.fnmatchcase(relative, pattern) for b, pattern in selectors) or any(relative.startswith(p + '/') for p in selected_dirs)
                if selected:
                    matches.add((base, entry))
                    if entry.is_dir() and not entry.is_symlink(): selected_dirs.append(relative)
    count = 0
    for base, entry in sorted(matches):
        entries = [entry]
        for entry in entries:
            relative = entry.relative_to(base)
            output_relative = pathlib.Path('temp') / relative if base != source else relative
            parent_fd = os.open(base, os.O_RDONLY | os.O_DIRECTORY | os.O_NOFOLLOW)
            try:
                for component in relative.parts[:-1]:
                    next_fd = os.open(component, os.O_RDONLY | os.O_DIRECTORY | os.O_NOFOLLOW, dir_fd=parent_fd)
                    os.close(parent_fd); parent_fd = next_fd
                metadata = os.stat(relative.name, dir_fd=parent_fd, follow_symlinks=False)
                target = destination / output_relative
                if stat.S_ISDIR(metadata.st_mode):
                    target.mkdir(parents=True, exist_ok=True)
                    continue
                if not stat.S_ISREG(metadata.st_mode):
                    raise ValueError('nonregular source artifact')
                fd = os.open(relative.name, os.O_RDONLY | os.O_NOFOLLOW | os.O_NONBLOCK, dir_fd=parent_fd)
                with os.fdopen(fd, 'rb') as data:
                    if not stat.S_ISREG(os.fstat(data.fileno()).st_mode):
                        raise ValueError('source artifact changed type')
                    target.parent.mkdir(parents=True, exist_ok=True)
                    if target.exists():
                        continue
                    with target.open('xb') as output:
                        while chunk := data.read(65536):
                            budget[0] += len(chunk)
                            if budget[0] > 100 * 1024 * 1024:
                                raise ValueError('source artifact byte bound')
                            output.write(chunk)
                    count += 1
                    if count > 10000:
                        raise ValueError('source artifact count bound')
            finally:
                os.close(parent_fd)
    return count


def cleanup_source_export(text):
    verify_gui_helper()
    config = json.loads(gui_operation('check'))
    path = pathlib.Path(text)
    state = pathlib.Path(config['stateDirectory'])
    if (os.geteuid() != config['collectorUid'] or path.parent != state or path.resolve() != path
            or not re.fullmatch('factory-source-artifacts-[a-z0-9_]{8}', path.name)):
        raise ValueError('source artifact cleanup path refused')
    if path.exists():
        metadata = path.lstat()
        if not stat.S_ISDIR(metadata.st_mode) or metadata.st_uid != config['collectorUid'] or metadata.st_mode & 0o077:
            raise ValueError('source artifact cleanup identity refused')
        shutil.rmtree(path)


def reconcile_source_exports(config):
    # A canceled artifact upload may not reach its final cleanup step. Preserve
    # recent exports for live uploaders, reclaim only exact old collector roots,
    # and bound retained storage instead of silently filling the machine.
    retained = 0
    for path in pathlib.Path(config['stateDirectory']).iterdir():
        if not path.name.startswith('factory-source-artifacts-'):
            continue
        if not re.fullmatch('factory-source-artifacts-[a-z0-9_]{8}', path.name):
            raise ValueError('unknown source artifact journal')
        metadata = path.lstat()
        if not stat.S_ISDIR(metadata.st_mode) or metadata.st_uid != config['collectorUid'] or metadata.st_mode & 0o077:
            raise ValueError('source artifact identity uncertain')
        if metadata.st_mtime < time.time() - 21600:
            cleanup_source_export(str(path))
        else:
            retained += 1
        if retained >= 64:
            raise ValueError('source artifact retention bound')


def exercise_source_recovery():
    """Fixed collector-only live acceptance probe; never accepts a path/program.
    It leaves its OWN legitimate journal once, then proves refusal of a known
    fixture-only uncertain record, restoring only that record before normal
    protected reconciliation. This models an unclean exit, not a literal SIGKILL.
    """
    global GUI_CONFIG, GUI_LEASE, EXECUTION_DEADLINE
    verify_gui_helper()
    code, manager = command(['/bin/launchctl', 'managername'], '/')
    if code or manager.strip() not in ('Background', 'System'):
        raise ValueError('acceptance collector must be non-GUI')
    GUI_CONFIG = json.loads(gui_operation('check'))
    if (os.geteuid() != GUI_CONFIG['collectorUid'] or GUI_CONFIG['collectorRepository'].lower() != os.environ.get('GITHUB_REPOSITORY', '').lower()
            or GUI_CONFIG['runnerName'] != os.environ.get('RUNNER_NAME')):
        raise ValueError('acceptance collector registration refused')
    EXECUTION_DEADLINE = time.monotonic() + 180
    os.environ['SIDE'] = 'base'
    reconcile_probe_fixture(GUI_CONFIG)
    guard = open(pathlib.Path(GUI_CONFIG['stateDirectory']) / 'probe.lock', 'a+b')
    os.chmod(guard.name, 0o600)
    try:
        fcntl.flock(guard.fileno(), fcntl.LOCK_EX | fcntl.LOCK_NB)
        return _exercise_source_recovery_locked()
    finally:
        guard.close()


def _exercise_source_recovery_locked():
    global GUI_LEASE
    # First leave a valid recorded workspace, after a fixed source command's
    # proven sweep. No domain setting, service or root quarantine is modified.
    with GuiLease(GUI_CONFIG) as lease:
        GUI_LEASE = lease
        with GuiWorkspace() as workspace:
            first = workspace.root
            (first / 'base').mkdir()
            principal = GuiPrincipal(first)
            workspace.handoff()
            code, _ = principal.run(['/usr/bin/true'], first)
            if code:
                principal.close()
                raise ValueError('fixed acceptance source command failed')
            gui_operation('sweep', first, cleanup=True)
            # Deliberate test-owned unclean exit: leave the valid active journal.
    with GuiLease(GUI_CONFIG) as lease:
        GUI_LEASE = lease
        if not lease.reconciled or first.exists():
            raise ValueError('unclean workspace was not reconciled')
        with GuiWorkspace() as workspace:
            uncertain = workspace.root
            gui_operation('baseline', uncertain)
            workspace.handoff()
            # This collector-owned known fixture record intentionally cannot
            # pass the root helper's root-owner/command-identity proof. It names
            # no real process group and must fail BEFORE any signal or source.
            probe = uncertain / 'gui-command-0.json'
            body = PROBE_UNCERTAINTY_BODY
            state_file = pathlib.Path(GUI_CONFIG['stateDirectory']) / 'probe-uncertainty.json'
            temporary = state_file.parent / ('probe-intent-' + uuid.uuid4().hex)
            fd = os.open(temporary, os.O_CREAT | os.O_EXCL | os.O_WRONLY, 0o600)
            with os.fdopen(fd, 'w') as output:
                json.dump({'version': 1, 'root': str(uncertain), 'configurationSha256': GUI_CONFIG['hash']}, output)
                output.flush(); os.fsync(output.fileno())
            os.replace(temporary, state_file)
            record_temporary = uncertain / ('acceptance-record-' + uuid.uuid4().hex)
            fd = os.open(record_temporary, os.O_CREAT | os.O_EXCL | os.O_WRONLY, 0o600)
            with os.fdopen(fd, 'w') as output:
                output.write(body)
                output.flush(); os.fsync(output.fileno())
            os.link(record_temporary, probe)  # complete, exclusive; never replace another record
            record_temporary.unlink()
    blocked = False
    source_started_while_blocked = False
    try:
        with GuiLease(GUI_CONFIG):
            source_started_while_blocked = True
    except ValueError:
        blocked = True
    if not blocked or source_started_while_blocked or not uncertain.exists():
        raise ValueError('uncertain cleanup admitted a replacement')
    # Restore ONLY our exact collector-owned fixture metadata, never a real
    # root-owned command record, another job's marker or a worker quarantine.
    metadata = probe.lstat()
    marker = pathlib.Path(GUI_CONFIG['stateDirectory']) / 'active.json'
    state = json.loads(marker.read_text())
    if (state != {'root': str(uncertain), 'devices': [], 'deviceIntents': []}
            or not stat.S_ISREG(metadata.st_mode) or metadata.st_uid != os.geteuid()
            or metadata.st_mode & 0o777 != 0o600 or probe.read_text() != body):
        raise ValueError('acceptance fixture changed; reuse stays held')
    probe.unlink()
    with GuiLease(GUI_CONFIG) as lease:
        GUI_LEASE = lease
        if not lease.reconciled or uncertain.exists():
            raise ValueError('restored fixture was not normally reconciled')
        with GuiWorkspace() as workspace:
            fresh = workspace.root
            (fresh / 'base').mkdir()
            principal = GuiPrincipal(fresh)
            workspace.handoff()
            try:
                code, _ = principal.run(['/usr/bin/true'], fresh)
                if code:
                    raise ValueError('fresh source command failed')
            finally:
                principal.close()
            evidence = lease.evidence()
    state_file.unlink()
    return {'version': 1, 'literalCrash': False, 'validUncleanJournalReconciled': True,
            'failedCleanupBlockedReuse': blocked, 'sourceStartedWhileBlocked': source_started_while_blocked,
            'restoredOnlyOwnFixture': True, 'normalReconciliationRestoredReuse': True,
            'freshWorkspace': len({str(first), str(uncertain), str(fresh)}) == 3,
            'configurationSha256': GUI_CONFIG['hash'], 'lease': evidence}


def source_job_main():
    global GUI_CONFIG, GUI_LEASE, EXECUTION_DEADLINE
    if os.environ.get('FACTORY_GUI_SOURCE_PROTOCOL') != SOURCE_PROTOCOL or len(sys.argv) != 1:
        raise ValueError('invalid installed source protocol')
    plan = source_plan(os.environ['FACTORY_GUI_SOURCE_PLAN'])
    repository = os.environ['FACTORY_GUI_SOURCE_REPOSITORY']
    sha = os.environ['FACTORY_GUI_SOURCE_SHA']
    if any(not re.fullmatch('[1-9][0-9]{0,20}', os.environ.get(key, '')) for key in ('FACTORY_GUI_SOURCE_RUN_ID', 'FACTORY_GUI_SOURCE_RUN_ATTEMPT')):
        raise ValueError('invalid source run identity')
    # Reuse the pinned helper's policy and actual collector registration checks.
    verify_gui_helper()
    code, manager = command(['/bin/launchctl', 'managername'], '/')
    if code or manager.strip() not in ('Background', 'System'):
        raise ValueError('source collector must be non-GUI')
    GUI_CONFIG = json.loads(gui_operation('check'))
    if (os.geteuid() != GUI_CONFIG['collectorUid'] or GUI_CONFIG['collectorRepository'].lower() != os.environ.get('GITHUB_REPOSITORY', '').lower()
            or GUI_CONFIG['runnerName'] != os.environ.get('RUNNER_NAME') or repository.lower() not in [r.lower() for r in GUI_CONFIG['trustedRepositories']]):
        raise ValueError('source collector identity or allowlist refused')
    if hashlib.sha256(GUI_HELPER.read_bytes()).hexdigest() != 'GUI_HELPER_HASH':
        raise ValueError('source helper is stale')
    EXECUTION_DEADLINE = time.monotonic() + plan['timeoutMinutes'] * 60
    os.environ['SIDE'] = 'base'
    succeeded = True
    export = None
    with GuiLease(GUI_CONFIG) as lease:
        GUI_LEASE = lease
        reconcile_source_exports(GUI_CONFIG)
        with GuiWorkspace() as workspace:
            root = workspace.root
            staging = root / 'archives'; staging.mkdir()
            source = root / 'base'; source.mkdir()
            source_archive(repository, sha, source, staging / 'source.zip')
            source_git_metadata(repository, sha, source, staging)
            if plan['submodules'] == 'recursive':
                source_submodules(repository, sha, source, staging)
            principal = GuiPrincipal(root)
            workspace.handoff()
            try:
                principal.share(source)
                temp = root / 'source-temp'; temp.mkdir(); principal.share(temp)
                for name in ('env', 'path', 'output'):
                    code, _ = principal.run(['/usr/bin/touch', str(temp / name)], source)
                    if code:
                        raise ValueError('source command files unavailable')
                common = source_environment(plan['env'], source, temp)
                common.update({'GITHUB_WORKSPACE': str(source), 'RUNNER_TEMP': str(temp), 'GITHUB_ENV': str(temp / 'env'), 'GITHUB_PATH': str(temp / 'path'), 'GITHUB_OUTPUT': str(temp / 'output'),
                               'GITHUB_REPOSITORY': repository, 'GITHUB_SHA': sha, 'GITHUB_RUN_ID': os.environ['FACTORY_GUI_SOURCE_RUN_ID'], 'GITHUB_RUN_ATTEMPT': os.environ['FACTORY_GUI_SOURCE_RUN_ATTEMPT']})
                for index, step in enumerate(plan['steps']):
                    dynamic, paths = source_command_state(principal, temp)
                    environment = dict(common, **dynamic)
                    if (step['condition'] == 'success()' and not succeeded) or (step['condition'] == "env.LW_INFERENCE_REAL == '1'" and (not succeeded or environment.get('LW_INFERENCE_REAL') != '1')):
                        continue
                    environment.update(source_environment(step['env'], source, temp))
                    environment['PATH'] = ':'.join(paths + ['/opt/homebrew/bin', '/usr/local/bin', '/usr/bin', '/bin', '/usr/sbin', '/sbin'])
                    script = root / ('source-step-' + str(index) + '.sh')
                    script.write_text(step['run']); script.chmod(0o644)
                    # The plan names the only shell semantics the runner ever applies.
                    # The renderer maps 'bash' to pipefail semantics and 'implicit-bash'
                    # to the same fixed bash without pipefail; no caller argv exists.
                    argv = (['/bin/bash', '--noprofile', '--norc', '-e', '-o', 'pipefail', str(script)] if step.get('shell') == 'bash'
                            else ['/bin/bash', '--noprofile', '--norc', '-e', str(script)])
                    code, output = principal.run(argv, source, extra_environment=environment, timeout=step['timeoutMinutes'] * 60)
                    # Source output is a data artifact, never Actions commands.
                    (root / ('source-step-' + str(index) + '.log')).write_text(output)
                    succeeded = succeeded and code == 0
                # Source has exited; quiesce before no-follow publication reads.
                gui_operation('sweep', root, cleanup=True)
                export = pathlib.Path(tempfile.mkdtemp(prefix='factory-source-artifacts-', dir=GUI_CONFIG['stateDirectory']))
                export.chmod(0o700)
                budget = [0]
                for index, artifact in enumerate(plan['artifacts']):
                    if artifact['condition'] == 'success()' and not succeeded:
                        continue
                    target = export / str(index); target.mkdir()
                    found = source_export(source, artifact['paths'], target, budget)
                    if found == 0 and artifact['ifNoFilesFound'] == 'error':
                        succeeded = False
                for log in root.glob('source-step-*.log'):
                    budget[0] += log.stat().st_size
                    if budget[0] > 100 * 1024 * 1024:
                        raise ValueError('source artifact byte bound')
                    shutil.copyfile(log, export / log.name)
                (export / 'receipt.json').write_text(json.dumps({'version': 1, 'repository': repository, 'sha': sha, 'workflow': plan['workflow'], 'jobId': plan['jobId'], 'succeeded': succeeded, 'configurationSha256': GUI_CONFIG['hash']}))
            finally:
                principal.close()
    if export is not None:
        with open(os.environ['GITHUB_OUTPUT'], 'a') as output:
            output.write('artifact_root=' + str(export) + '\n')
    return 0 if succeeded else 1


if __name__ == '__main__':
    signal.signal(signal.SIGINT, interrupt)
    signal.signal(signal.SIGTERM, interrupt)
    try:
        if len(sys.argv) == 2 and sys.argv[1] == '--exercise-recovery':
            receipt = exercise_source_recovery()
            export = pathlib.Path(tempfile.mkdtemp(prefix='factory-source-artifacts-', dir=GUI_CONFIG['stateDirectory']))
            export.chmod(0o700)
            (export / 'recovery-probe.json').write_text(json.dumps(receipt))
            with open(os.environ['GITHUB_OUTPUT'], 'a') as output:
                output.write('artifact_root=' + str(export) + '\n')
            sys.exit(0)
        if len(sys.argv) == 3 and sys.argv[1] == '--cleanup-export':
            cleanup_source_export(sys.argv[2])
            sys.exit(0)
        sys.exit(source_job_main())
    except Exception:
        print('factory-gui-source-job: execution or cleanup refused', file=sys.stderr)
        sys.exit(70)
