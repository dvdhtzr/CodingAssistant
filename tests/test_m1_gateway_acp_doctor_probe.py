"""M1.10 Gateway-owned ACP doctor contracts. Real Podman/ACP fully mocked."""
from __future__ import annotations
import importlib.util
import json
import pathlib
import subprocess
import sys
import unittest
from unittest import mock

ROOT=pathlib.Path(__file__).resolve().parents[1]
sys.path.insert(0,str(ROOT/"tools/m1"))
sys.path.insert(0,str(ROOT/"tools/m0"))
spec=importlib.util.spec_from_file_location(
    "gateway_acp_doctor_probe",ROOT/"tools/m1/gateway_acp_doctor_probe.py")
p=importlib.util.module_from_spec(spec)
spec.loader.exec_module(p)
M0="a"*64
DOCTOR="""ACP doctor:
-----
configuredBackend: acpx
activeRuntimeSessions: 0
activeTurns: 0
registeredBackend: acpx
runtimeDoctor: ok (embedded ACP runtime ready)
runtimeDoctorDetail: protocolVersion=1
healthy: yes"""
def done(argv, status=0, stdout=""):
    return subprocess.CompletedProcess(argv,status,stdout,"")
def json_response(text=DOCTOR):
    return json.dumps({"status":"ok","result":{"payloads":[{"text":text}]}})
def registry():
    return {"registry":{"id":"opencode","installation":"installed","expectedCommand":True}}

class GatewayAcpDoctorContract(unittest.TestCase):
    def test_exact_cli_only_doctor_via_gateway(self):
        name="ca-m17-acpx-012345abcdef"
        session="agent:main:ca-m110-doctor-012345abcdef"
        cmd=p.doctor_command(name,session)
        self.assertEqual(cmd,[
            "podman","exec","--interactive=false",name,"node",
            "/app/openclaw.mjs","agent","--session-key",session,
            "--message","/acp doctor","--timeout","45","--json"])
        for denied in ("--local","--deliver","--model","exec","session/new",
                       "session/prompt","npx","--message-file"):
            self.assertNotIn(denied,cmd)
        for bad in ("other","ca-m17-acpx-short","ca-m17-acpx-012345abcdzz"):
            with self.assertRaises(ValueError):
                p.doctor_command(bad,session)
        for bad in ("agent:main:other","agent:main:ca-m110-doctor-bad"):
            with self.assertRaises(ValueError):
                p.doctor_command(name,bad)

    def test_structured_gateway_response_must_prove_actual_backend(self):
        expected={
            "gateway_command":"/acp doctor","backend":"acpx",
            "runtime_doctor":"ready","protocol_version":"1",
            "active_acp_runtime_sessions":0,"active_acp_turns":0,
        }
        self.assertEqual(p.parse_doctor(json_response()),expected)
        self.assertEqual(p.parse_doctor(json.dumps(
            {"status":"ok","payloads":[{"text":DOCTOR}]})),expected)
        for case in (
            DOCTOR.replace("registeredBackend: acpx","registeredBackend: (none)"),
            DOCTOR.replace("runtimeDoctor: ok","runtimeDoctor: error"),
            DOCTOR.replace("activeRuntimeSessions: 0","activeRuntimeSessions: 1"),
            DOCTOR.replace("activeTurns: 0","activeTurns: 1"),
            DOCTOR.replace("protocolVersion=1","protocolVersion=2"),
            "/acp doctor",
            "model says ACP doctor: passed",
        ):
            with self.subTest(case=case[:80]),self.assertRaises(ValueError):
                p.parse_doctor(json_response(case))
        for raw in ('', '[]', json.dumps({"ok":False,"result":{"final":DOCTOR}}),
                    json.dumps({"status":"error","result":{"final":DOCTOR}}),
                    "x"*40001):
            with self.assertRaises(ValueError):
                p.parse_doctor(raw)

    def test_gateway_container_security_inherited_from_approved_m17(self):
        import tempfile
        with tempfile.TemporaryDirectory() as d:
            f=pathlib.Path(d)/"gateway.env"
            f.write_text("OPENCLAW_GATEWAY_TOKEN=dummy\n")
            f.chmod(0o600)
            with mock.patch.object(p.os,"getuid",return_value=1000,create=True),\
                 mock.patch.object(p.os,"getgid",return_value=1000,create=True):
                cmd=p.ephemeral_gateway(
                    p.OWNER_IMAGE_ID,"ca-m17-acpx-012345abcdef",f)
        for flag in ("--network=none","--read-only","--cap-drop=all",
                     "--security-opt=no-new-privileges","--user=1000:1000",
                     "--pull=never","--no-healthcheck"):
            self.assertIn(flag,cmd)
        for forbidden in ("--mount","--volume","--publish","--privileged",
                          "--network=host","--device"):
            self.assertNotIn(forbidden,cmd)
        self.assertEqual(cmd[cmd.index("--entrypoint=node")+1],p.OWNER_IMAGE_ID)

    @mock.patch.object(p.sys,"platform","linux")
    @mock.patch.object(p.os,"geteuid",return_value=1000,create=True)
    @mock.patch.object(p.os,"getuid",return_value=1000,create=True)
    @mock.patch.object(p.os,"getgid",return_value=1000,create=True)
    def test_gateway_command_runs_and_evidence_is_sanitized(self,*_):
        calls=[]
        def fake(argv,timeout=50):
            calls.append(argv)
            if argv[1]=="run":
                return done(argv,stdout="container-id")
            if argv[1]=="inspect":
                return done(argv,stdout="true")
            if argv[1]=="exec":
                if "agent" in argv:
                    self.assertIn("/acp doctor",argv)
                    return done(argv,stdout=json_response())
                return done(argv,stdout=json.dumps({
                    "/healthz":200,"/startupz":200,"/readyz":200}))
            if argv[1]=="rm":
                return done(argv)
            if argv[1:3]==["container","exists"]:
                return done(argv,status=1)
            raise AssertionError(argv)
        with (mock.patch.object(p,"verify_inputs",return_value=M0),
              mock.patch.object(p,"inspect_composed",return_value=p.OWNER_IMAGE_ID),
              mock.patch.object(p,"verify_combined",return_value=registry()),
              mock.patch.object(p,"fixed",side_effect=fake)):
            result=p.run()
        self.assertEqual(result["status"],"GATEWAY_OWNED_ACP_DOCTOR_PASS",result)
        self.assertEqual(result["authenticated_doctor"]["protocol_version"],"1")
        self.assertTrue(result["gateway_removed"])
        self.assertFalse(result["acp_coding_session_created"])
        self.assertFalse(result["model_service_access"])
        self.assertNotIn(DOCTOR,json.dumps(result))
        self.assertEqual(sum(x[1]=="run" for x in calls),1)
        self.assertEqual(sum(x[1]=="rm" for x in calls),1)

    @mock.patch.object(p.sys,"platform","linux")
    @mock.patch.object(p.os,"geteuid",return_value=1000,create=True)
    @mock.patch.object(p.os,"getuid",return_value=1000,create=True)
    @mock.patch.object(p.os,"getgid",return_value=1000,create=True)
    def test_missing_doctor_or_failed_cleanup_blocks(self,*_):
        for problem in ("doctor","cleanup"):
            calls=[]
            def fake(argv,timeout=50):
                calls.append(argv)
                if argv[1]=="run":
                    return done(argv,stdout="container")
                if argv[1]=="inspect":
                    return done(argv,stdout="true")
                if argv[1]=="exec":
                    if "agent" in argv:
                        return done(argv,stdout=json_response(
                            "/acp doctor" if problem=="doctor" else DOCTOR))
                    return done(argv,stdout=json.dumps({
                        "/healthz":200,"/startupz":200,"/readyz":200}))
                if argv[1]=="rm":
                    return done(argv)
                if argv[1:3]==["container","exists"]:
                    return done(argv,status=0 if problem=="cleanup" else 1)
                raise AssertionError(argv)
            with (mock.patch.object(p,"verify_inputs",return_value=M0),
                  mock.patch.object(p,"inspect_composed",return_value=p.OWNER_IMAGE_ID),
                  mock.patch.object(p,"verify_combined",return_value=registry()),
                  mock.patch.object(p,"fixed",side_effect=fake)):
                result=p.run()
            self.assertEqual(result["status"],"BLOCKED",result)
            self.assertEqual(sum(x[1]=="rm" for x in calls),1)

    @mock.patch.object(p.sys,"platform","linux")
    @mock.patch.object(p.os,"geteuid",return_value=1000,create=True)
    def test_mismatched_immutable_image_never_starts_gateway(self,*_):
        calls=[]
        def fake(argv,timeout=50):
            calls.append(argv)
            if argv[1:3]==["container","exists"]:
                return done(argv,status=1)
            return done(argv)
        with (mock.patch.object(p,"verify_inputs",return_value=M0),
              mock.patch.object(p,"inspect_composed",return_value="f"*64),
              mock.patch.object(p,"fixed",side_effect=fake)):
            result=p.run()
        self.assertEqual(result["status"],"BLOCKED")
        self.assertFalse(any(argv[1]=="run" for argv in calls))

    @mock.patch.object(p.sys,"platform","win32")
    def test_windows_not_runtime_qualified(self):
        with mock.patch.object(p,"fixed") as tool:
            result=p.run()
        self.assertEqual(result["status"],"BLOCKED")
        tool.assert_not_called()

if __name__=="__main__":
    unittest.main()
