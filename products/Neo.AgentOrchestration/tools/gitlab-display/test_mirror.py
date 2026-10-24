import copy
import json
import os
from pathlib import Path
import tempfile
import unittest
import uuid
from mirror import (BEGIN, END, STATES, LABEL_PREFIX, HttpFailure, MirrorError,
                    Store, marker, payload, project_item, read_source, replace_block,
                    safe_title, sync, validate_url)

PROJECT='00000000-0000-0000-0000-000000000001'
ITEM='00000000-0000-0000-0000-000000000004'
KEY=b'k'*32
BIND={'organization_id':'00000000-0000-0000-0000-000000000002',
      'workspace_id':'00000000-0000-0000-0000-000000000003','project_id':PROJECT,
      'project_key':'NEO-ORCH','gitlab_project_id':1,'gitlab_project_path':'fanasa/neo-framework',
      'gitlab_url':'https://gitlab.example','web_url':'https://work.example','board_url':'https://gitlab.example/fanasa/neo-framework/-/boards/1'}
RAW={'id':ITEM,'projectId':PROJECT,'key':'NAO-001','title':'نمایش امن کانبان',
     'status':'Ready','priority':'Normal','elapsedSeconds':60,'estimatedSeconds':3600,
     'estimatedTokens':500,'tokenMeter':{'knownTotalTokens':25,'totalTokens':None},
     'description':'PRIVATE-CHAT-CANARY','ownerAgentId':'PRIVATE-AGENT-CANARY',
     'ownerChatId':'PRIVATE-OWNER-CANARY','acceptanceCriteria':'PRIVATE-ACCEPTANCE-CANARY'}

class Target:
    def __init__(self):self.issues=[];self.calls=[];self.lost=False;self.reject=None
    def call(self,method,path,body=None):
        self.calls.append((method,path,body))
        if self.reject:raise HttpFailure(self.reject)
        if method=='POST':
            r={'iid':len(self.issues)+1,'project_id':1,'author':{'id':77},'state':'opened',
               'title':body['title'],'description':body['description'],'labels':body['labels'].split(','),
               'confidential':body['confidential']}
            self.issues.append(r)
            if self.lost:raise MirrorError('response lost')
            return copy.deepcopy(r)
        r=self.issues[int(path.rsplit('/',1)[1])-1]
        r.update({k:v for k,v in body.items() if k in ('title','description','confidential')})
        labs=set(r['labels']);labs.update(filter(None,body['add_labels'].split(',')))
        labs.difference_update(filter(None,body['remove_labels'].split(',')));r['labels']=sorted(labs)
        if body.get('state_event'):r['state']='closed' if body['state_event']=='close' else 'opened'
        return copy.deepcopy(r)

class MirrorTests(unittest.TestCase):
    def setUp(self):
        self.tmp=tempfile.TemporaryDirectory(dir=os.environ.get('FANASA_TEST_TMP_DIRECTORY'));self.addCleanup(self.tmp.cleanup)
        self.db=Store(Path(self.tmp.name)/'map.db',BIND);self.addCleanup(self.db.db.close)
        self.t=Target();self.i=project_item(RAW,PROJECT)
    def run_sync(self,items=None,remote=None):
        return sync(items or [self.i],copy.deepcopy(self.t.issues) if remote is None else remote,self.t,BIND,KEY,self.db,77)
    def test_allowlist_no_private_fields(self):
        rendered=json.dumps(payload(self.i,BIND,KEY))+json.dumps(self.i)
        for canary in ['PRIVATE-CHAT-CANARY','PRIVATE-AGENT-CANARY','PRIVATE-OWNER-CANARY','PRIVATE-ACCEPTANCE-CANARY']:
            self.assertNotIn(canary,rendered)
        self.assertNotIn('description',self.i)
    def test_scrub_obvious_secrets_and_identifiers(self):
        s=safe_title('test password=SECRET sk-123456789abcdef user@example.com C:\\private\\foo https://a.invalid/?secret=x')
        for forbidden in ['SECRET','123456789abcdef','user@example.com','private','secret=x']:self.assertNotIn(forbidden,s)
    def test_title_controls_and_markup(self):
        s=safe_title('<script> [link] `x`\u202e\n hello')
        for bad in ['<','>','[',']','`','\u202e','\n']:self.assertNotIn(bad,s)
    def test_unknown_token_total_remains_unknown(self):
        r=payload(self.i,BIND,KEY)['description']
        self.assertIn('مصرف کامل گزارش‌شدهٔ توکن | نامشخص',r)
        self.assertIn('زیرجمع توکن‌های معلوم | 25',r)
    def test_missing_estimates_not_zero(self):
        self.i['estimatedTokens']=self.i['estimatedSeconds']=None
        r=payload(self.i,BIND,KEY)['description'];self.assertIn('برآورد توکن | نامشخص',r)
    def test_foreign_project_rejected(self):
        with self.assertRaises(MirrorError):project_item(RAW,str(uuid.uuid4()))
    def test_invalid_status_rejected(self):
        with self.assertRaises(MirrorError):project_item(dict(RAW,status='untrusted'),PROJECT)
    def test_invalid_counter_rejected(self):
        for n in (-1,True,'1'):
            with self.assertRaises(MirrorError):project_item(dict(RAW,elapsedSeconds=n),PROJECT)
    def test_invalid_key_rejected(self):
        with self.assertRaises(MirrorError):project_item(dict(RAW,key='[unsafe](link)'),PROJECT)
    def test_one_issue_and_unchanged_rerun(self):
        self.assertEqual(1,self.run_sync()['created'])
        self.assertEqual(0,self.run_sync()['changed']);self.assertEqual(1,len(self.t.issues))
    def test_status_update_no_duplicate(self):
        self.run_sync();self.i['status']='InProgress';self.assertEqual(1,self.run_sync()['changed'])
        self.assertEqual([LABEL_PREFIX+STATES['InProgress'][0]],self.t.issues[0]['labels'])
        self.assertEqual(1,len(self.t.issues))
    def test_done_and_cancelled_closed(self):
        for state in ('Done','Cancelled'):
            self.i['status']=state;self.run_sync();self.assertEqual('closed',self.t.issues[0]['state'])
    def test_reopen_when_source_reopens(self):
        self.i['status']='Done';self.run_sync();self.i['status']='Ready';self.run_sync()
        self.assertEqual('opened',self.t.issues[0]['state'])
    def test_archive_existing_without_deletion(self):
        self.run_sync();self.i['archived']=True;self.run_sync()
        self.assertEqual('closed',self.t.issues[0]['state']);self.assertEqual(1,len(self.t.issues))
    def test_never_publish_new_archived(self):
        self.i['archived']=True;self.assertEqual(1,self.run_sync()['unpublishedArchived']);self.assertEqual([],self.t.calls)
    def test_loss_after_create_recovers_mapping(self):
        self.t.lost=True
        with self.assertRaises(MirrorError):self.run_sync()
        self.assertEqual((None,1),self.db.get(ITEM));self.t.lost=False
        self.assertEqual(1,self.run_sync()['recoveredMappings']);self.assertEqual(1,len(self.t.issues))
    def test_pending_without_receipt_not_retried(self):
        self.db.pending(ITEM)
        with self.assertRaises(MirrorError):self.run_sync()
        self.assertEqual([],self.t.calls)
    def test_rejected_create_not_uncertain(self):
        self.t.reject=403
        with self.assertRaises(HttpFailure):self.run_sync()
        self.assertEqual((None,0),self.db.get(ITEM))
    def test_missing_mapped_issue_not_recreated(self):
        self.run_sync()
        with self.assertRaises(MirrorError):self.run_sync(remote=[])
        self.assertEqual(1,len(self.t.issues))
    def test_spoofed_marker_author_denied(self):
        self.run_sync();self.t.issues[0]['author']['id']=88
        with self.assertRaises(MirrorError):self.run_sync()
    def test_duplicate_marker_denied(self):
        self.run_sync();self.t.issues.append(copy.deepcopy(self.t.issues[0]))
        with self.assertRaises(MirrorError):self.run_sync()
    def test_unrelated_issue_untouched(self):
        other={'iid':999,'author':{'id':99},'description':'unrelated'}
        remote=[other];self.run_sync(remote=remote);self.assertEqual(other,remote[0])
    def test_unmanaged_notes_and_labels_preserved(self):
        self.run_sync();self.t.issues[0]['description']+='\nHuman note.';self.t.issues[0]['labels'].append('custom')
        self.i['status']='Review';self.run_sync();r=self.t.issues[0]
        self.assertTrue(r['description'].endswith('Human note.'));self.assertIn('custom',r['labels'])
    def test_changed_block_denied(self):
        self.run_sync();self.t.issues[0]['description']=self.t.issues[0]['description'].replace(END,'')
        with self.assertRaises(MirrorError):self.run_sync()
    def test_mapping_bound_to_scope(self):
        with self.assertRaises(MirrorError):Store(Path(self.tmp.name)/'map.db',dict(BIND,workspace_id=str(uuid.uuid4())))
    def test_marker_different_scope_and_key(self):
        self.assertNotEqual(marker(BIND,ITEM,KEY),marker(dict(BIND,project_id=str(uuid.uuid4())),ITEM,KEY))
        self.assertNotEqual(marker(BIND,ITEM,KEY),marker(BIND,ITEM,b'x'*32))
    def test_url_no_credential_redirect_destination(self):
        for bad in ['http://evil.test','https://u:p@host','https://host?a=1','https://host#fragment','http://localhost']:
            with self.assertRaises(MirrorError):validate_url(bad,True)
        self.assertEqual('http://127.0.0.1:18110',validate_url('http://127.0.0.1:18110/',True))
    def test_pagination_incomplete_prevents_publication(self):
        class Source:
            def call(self,m,p):
                if p=='/catalog':return {'projects':[{'id':PROJECT,'key':'NEO-ORCH','isEnabled':True}]}
                return {'items':[],'total':1}
        with self.assertRaises(MirrorError):read_source(Source(),BIND)
    def test_budget_bound(self):
        with self.assertRaises(MirrorError):sync([self.i],[],self.t,BIND,KEY,self.db,77,max_changes=0)
        self.assertEqual([],self.t.calls)

if __name__=='__main__':unittest.main()
