"""M1.11 Gateway-owned diagnostic RPC model-free contract checks."""
import importlib.util
import json
import pathlib
import subprocess
import sys
import tempfile
import unittest
from unittest import mock

ROOT=pathlib.Path(__file__).resolve().parents[1]
sys.path[:0]=[str(ROOT/"tools/m1"),str(ROOT/"tools/m0")]
spec=importlib.util.spec_from_file_location("gateway_backend_rpc_probe",
                                            ROOT/"tools/m1/gateway_backend_rpc_probe.py")
p=importlib.util.module_from_spec(spec)
spec.loader.exec_module(p)
M0="a"*64
DERIVED="f"*64

def done(argv,code=0,out=""):
    return subprocess.CompletedProcess(argv,code,out,"")

def meta():
    return [{"Id":"sha256:"+DERIVED,"RepoTags":[p.TAG],
             "Labels":{p.LABEL_PREFIX+".base":p.OWNER_IMAGE_ID,
                       p.LABEL_PREFIX+".sources":p.source_fingerprint(),
                       p.LABEL_PREFIX+".method":p.METHOD}}]

class RegisteredBackendDoctorTests(unittest.TestCase):
    def test_plugin_is_exact_gateway_registered_backend_no_agent_turn(self):
        js=(p.PLUGIN_DIR/"index.mjs").read_text()
        for item in ('getAcpRuntimeBackend("acpx")','await backend.runtime.doctor()',
                     '"codingassistant.acp.doctor"','scope: "operator.read"',
                     'Object.keys(params).length !== 0','version !== "1"'):
            self.assertIn(item,js)
        for item in ("child_process","session/new","session/prompt","npx","fetch("):
            self.assertNotIn(item,js)
        cfg=json.loads(p.CONFIG_FILE.read_text())
        acpx=cfg["plugins"]["entries"]["acpx"]["config"]
        self.assertEqual(acpx["agents"]["opencode"],
                         {"command":"/usr/local/bin/opencode","args":["acp"]})
        self.assertEqual(acpx["probeAgent"],"opencode")
        self.assertEqual(acpx["agentProcessEnvAllow"],[])
        self.assertEqual(acpx["permissionMode"],"deny-all")
        self.assertEqual(acpx["nonInteractivePermissions"],"deny")
        self.assertEqual(cfg["acp"]["allowedAgents"],["opencode"])
        self.assertEqual(len(p.source_fingerprint()),64)

    def test_no_network_copy_only_no_mutating_parent_image(self):
        docker=p.CONTAINERFILE.read_text()
        self.assertIn("FROM "+"$"+"{BASE_IMAGE}",docker)
        self.assertIn("COPY doctor-plugin/",docker)
        self.assertIn("COPY doctor-config.json",docker)
        self.assertFalse(any(line.lstrip().startswith(("RUN ","ADD "))
                             for line in docker.splitlines()))
        args=p.build_command(p.source_fingerprint())
        for value in ("--network=none","--pull=never",
                      "BASE_IMAGE="+p.OWNER_IMAGE_ID):
            self.assertIn(value,args)
        self.assertNotIn("--volume",args)
        self.assertNotIn("--push",args)
        with self.assertRaises(ValueError):
            p.build_command("latest")

    def test_exact_local_derived_image_provenance(self):
        with mock.patch.object(p,"fixed",return_value=done([],out=json.dumps(meta()))):
            self.assertEqual(p.inspect_image(p.source_fingerprint()),DERIVED)
        for data in ([dict(meta()[0],Labels={})],
                     [dict(meta()[0],Id="sha256:"+p.OWNER_IMAGE_ID)],
                     [dict(meta()[0],RepoTags=["wrong"])],
                     [dict(meta()[0],Id="bad")]):
            with mock.patch.object(p,"fixed",return_value=done([],out=json.dumps(data))):
                with self.assertRaises(ValueError):
                    p.inspect_image(p.source_fingerprint())

    def test_exact_gateway_rpc_only_not_slash_model(self):
        name="ca-m17-acpx-012345abcdef"
        self.assertEqual(p.rpc_command(name),[
            "podman","exec","--interactive=false",name,"node",
            "/app/openclaw.mjs","gateway","call",p.METHOD,
            "--params","{}","--json","--timeout","60000"])
        for name in ("other","ca-m17-acpx-bad"):
            with self.assertRaises(ValueError):
                p.rpc_command(name)
        self.assertEqual(p.parse_result(json.dumps(p.EXPECTED))["protocol_version"],"1")
        for v in (dict(p.EXPECTED,protocolVersion="2"),
                  dict(p.EXPECTED,runtimeDoctorOk=False),
                  dict(p.EXPECTED,codingSessionCreated=True),
                  dict(p.EXPECTED,promptSent=True),
                  dict(p.EXPECTED,runtimeDoctorOk="true"),
                  dict(p.EXPECTED,extra="secret"),
                  []):
            with self.assertRaises(ValueError):
                p.parse_result(json.dumps(v))

    def test_gateway_stays_sealed_and_uses_image_only_config(self):
        with tempfile.TemporaryDirectory() as d:
            f=pathlib.Path(d)/"private.env"
            f.write_text("OPENCLAW_GATEWAY_TOKEN=private\n")
            f.chmod(0o600)
            with mock.patch.object(p.os,"getuid",return_value=1000,create=True),\
                 mock.patch.object(p.os,"getgid",return_value=1000,create=True):
                argv=p.gateway_command(DERIVED,"ca-m17-acpx-012345abcdef",f)
        for flag in ("--network=none","--read-only","--cap-drop=all",
                     "--security-opt=no-new-privileges","--pull=never",
                     "--env=OPENCLAW_CONFIG_PATH=/app/codingassistant-doctor-config.json"):
            self.assertIn(flag,argv)
        for forbidden in ("--mount","--volume","--publish","--privileged"):
            self.assertNotIn(forbidden,argv)
        self.assertEqual(argv[argv.index("--env=OPENCLAW_CONFIG_PATH=/app/codingassistant-doctor-config.json")+1],DERIVED)

    def test_real_gateway_rpc_mock_all_checks_and_cleanup(self):
        calls=[]
        def fake(argv,timeout=50):
            calls.append(argv)
            if argv[1]=="run":return done(argv,out="container")
            if argv[1]=="inspect":return done(argv,out="true")
            if argv[1]=="exec":
                if p.METHOD in argv:return done(argv,out=json.dumps(p.EXPECTED))
                return done(argv,out=json.dumps({
                    "/healthz":200,"/startupz":200,"/readyz":200}))
            if argv[1]=="rm":return done(argv)
            if argv[1:3]==["container","exists"]:return done(argv,code=1)
            raise AssertionError(argv)
        with mock.patch.object(p,"fixed",side_effect=fake),\
             mock.patch.object(p.os,"getuid",return_value=1000,create=True),\
             mock.patch.object(p.os,"getgid",return_value=1000,create=True):
            x=p.verify_gateway_rpc(DERIVED)
        self.assertEqual(x["doctor"]["backend"],"acpx")
        self.assertTrue(x["removed"])
        self.assertEqual(sum(c[1]=="run" for c in calls),1)
        self.assertEqual(sum(c[1]=="rm" for c in calls),1)

    def test_rpc_unavailable_malformed_and_cleanup_fail_closed(self):
        for problem in ("missing","invalid","cleanup"):
            def fake(argv,timeout=50):
                if argv[1]=="run":return done(argv,out="container")
                if argv[1]=="inspect":return done(argv,out="true")
                if argv[1]=="exec":
                    if p.METHOD in argv:
                        if problem=="missing":return done(argv,code=1,out='{"error":"hidden"}')
                        return done(argv,out=json.dumps(dict(p.EXPECTED,protocolVersion="2"
                                                              if problem=="invalid" else "1")))
                    return done(argv,out=json.dumps({
                        "/healthz":200,"/startupz":200,"/readyz":200}))
                if argv[1]=="rm":return done(argv)
                if argv[1:3]==["container","exists"]:
                    return done(argv,code=0 if problem=="cleanup" else 1)
                raise AssertionError(argv)
            with mock.patch.object(p,"fixed",side_effect=fake),\
                 mock.patch.object(p.os,"getuid",return_value=1000,create=True),\
                 mock.patch.object(p.os,"getgid",return_value=1000,create=True):
                with self.subTest(problem=problem),self.assertRaises(ValueError):
                    p.verify_gateway_rpc(DERIVED)

    @mock.patch.object(p.sys,"platform","win32")
    def test_windows_never_qualifies_real_gateway(self):
        with mock.patch.object(p,"fixed") as command:
            self.assertEqual(p.evaluate(True)["status"],"BLOCKED")
        command.assert_not_called()

    @mock.patch.object(p.sys,"platform","linux")
    @mock.patch.object(p.os,"geteuid",return_value=1000,create=True)
    def test_absent_derived_image_requires_explicit_prepare(self,*_):
        with mock.patch.object(p,"verify_inputs",return_value=M0),\
             mock.patch.object(p,"inspect_composed",return_value=p.OWNER_IMAGE_ID),\
             mock.patch.object(p,"verify_combined",return_value={
               "registry":{"id":"opencode","installation":"installed",
                           "expectedCommand":True}}),\
             mock.patch.object(p,"inspect_image",return_value=None),\
             mock.patch.object(p,"build_command") as build:
            status=p.evaluate(False)
        self.assertEqual(status["status"],"BLOCKED")
        self.assertIn("--prepare",status["reason"])
        build.assert_not_called()

if __name__=="__main__":
    unittest.main()
