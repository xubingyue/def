﻿using System;
using System.Collections.Generic;
using System.Linq;
using System.Xml;
using Photon.SocketServer;
using Eb;
using Es;
using Zk;

public class LoginApp<T> : Component<T> where T : ComponentDef, new()
{
    //-------------------------------------------------------------------------
    public Dictionary<string, LoginNode<ComponentDef>> mServerGroup = new Dictionary<string, LoginNode<ComponentDef>>();
    UCenterApp mUCenterApp = (UCenterApp)ApplicationBase.Instance;
    UCenterZkWatcher mZkWatcher;
    public string mServersPath = "";

    //-------------------------------------------------------------------------
    public uint NodeId { get { return mUCenterApp.NodeId; } }
    public string NodeIdStr { get { return mUCenterApp.NodeIdStr; } }

    //-------------------------------------------------------------------------
    public override void init()
    {
        EbLog.Note("LoginApp.init()");

        EntityMgr.getDefaultEventPublisher().addHandler(Entity);

        mServersPath ="/" + mUCenterApp.ProjectName + "/"
            + _eConstLoginNode.LoginServices.ToString();

        mUCenterApp.ZkClient.subscribeChildChanges(mServersPath, onServerGroupChange);

        mZkWatcher = new UCenterZkWatcher(this);
        PhotonApp photon_app = (PhotonApp)ApplicationBase.Instance;
        photon_app.ZkClient.addHandler(mZkWatcher.handler);
    }

    //-------------------------------------------------------------------------
    public override void release()
    {
        EbLog.Note("LoginApp.release()");
    }

    //-------------------------------------------------------------------------
    public override void update(float elapsed_tm)
    {
    }

    //-------------------------------------------------------------------------
    public override void handleEvent(object sender, EntityEvent e)
    {
        if (e is EvMainSessionEvent)
        {
            EvMainSessionEvent ev = (EvMainSessionEvent)e;
            byte local_type = (byte)ev.Se.node_type_local;
            byte remote_type = (byte)ev.Se.node_type_remote;

            if (ev.Se.connect)
            {
                // 连接消息
                Dictionary<string, object> cache_data = new Dictionary<string, object>();
                cache_data["RemoteSession"] = ev.Se.session;
                Entity et_ucentersession = EntityMgr.createEmptyEntity(
                    _eUCenterEtType.EtUCenterSession.ToString(), cache_data, Entity);
                et_ucentersession.addComponent<LoginUCenterSession<DefUCenterSession>>();
            }
            else
            {
                // 断开消息

                // do nothing
            }
        }
    }

    //-------------------------------------------------------------------------
    public void onServerGroupChange(int result, string data, string[] chdn, Dictionary<string, object> param)
    {
        if (result != 0) return;

        List<string> remoteServer = chdn.ToList();
        List<string> localServer = mServerGroup.Keys.ToList<string>();
        IEnumerable<string> add = remoteServer.Except(localServer);
        IEnumerable<string> del = localServer.Except(remoteServer);

        foreach (var sv in del)
        {
            // todo: 取消监听.
            mServerGroup.Remove(sv);
        }

        foreach (var sv in add)
        {
            Dictionary<string, object> cache_data = new Dictionary<string, object>();
            cache_data["parent"] = this;
            cache_data["ProjectName"] = mUCenterApp.ProjectName;
            cache_data["ServerGroupName"] = sv;
            Entity et_node = EntityMgr.createEmptyEntity("EtNode", cache_data, null);
            LoginNode<ComponentDef> co_node = et_node.addComponent<LoginNode<ComponentDef>>();

            Dictionary<string, object> pa = new Dictionary<string, object>();
            pa["LoginNode"] = co_node;
            mUCenterApp.ZkClient.areadData("/" + mUCenterApp.ProjectName + "/" + _eConstLoginNode.LoginServices.ToString() + "/" + sv, false, _onGetServersLoginNeedInfo, pa);

        }
    }

    private void _onGetServersLoginNeedInfo(int result, string data, string[] chdn, Dictionary<string, object> param)
    {
        if(result != 0 ) return ;
        LoginNode<ComponentDef> co_node = param["LoginNode"] as LoginNode<ComponentDef>;
        XmlDocument doc = new XmlDocument();
        doc.LoadXml(data);
        XmlNode zkinfo = doc.SelectSingleNode("ServerInfo/Login");
        string loginNode = zkinfo.Attributes["LoginNode"].Value;
        string  connStr = zkinfo.Attributes["ConnectionString"].Value;
        co_node.onGetLoginInfo(loginNode, connStr);

        Dictionary<string, Object> pa = new Dictionary<string, object>();
        pa["LoginNode"] = co_node;
        pa["path"] = loginNode;
        mUCenterApp.ZkClient.subscribeChildChanges(loginNode, mZkWatcher._onServerNodeChange, pa);

    }

    //-------------------------------------------------------------------------
    public override void onChildInit(Entity child)
    {
    }

    //-------------------------------------------------------------------------
    public IZkClient getZk()
    {
        return mUCenterApp.ZkClient;
    }


    //-------------------------------------------------------------------------
    public bool addLoginPlayer(string serverGroup, string account, string password, string channel, IComponent s)
    {
        LoginNode<ComponentDef> serverinfo;
        if (mServerGroup.TryGetValue(serverGroup, out serverinfo))
        {
            return serverinfo.addLoginPlayer(account, password, channel, s);
        }
        return false;
    }

    //-------------------------------------------------------------------------
    public void onGateBack(string server, string account, string result , Dictionary<byte , object> param)
    {
        LoginNode<ComponentDef> serverinfo;
        if (mServerGroup.TryGetValue(server, out serverinfo))
        {
            serverinfo.gateBackPlayerLoginResult(account, result, param);
        }
    }
}
