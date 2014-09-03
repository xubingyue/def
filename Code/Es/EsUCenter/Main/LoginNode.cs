﻿using System;
using System.Collections.Generic;
using System.Collections.Concurrent;
using System.Data;
using System.IO;
using System.Linq;
using System.Net;
using System.Text;
using System.Threading;
using System.Runtime.InteropServices;
using System.Xml;
using Photon.SocketServer;
using Photon.SocketServer.Diagnostics;
using Photon.SocketServer.ServerToServer;
using MySql.Data;
using MySql.Data.MySqlClient;
using Eb;
using Es;
using Zk;

enum eSERVER
{
    GATE_SERVER = 0,
    ZONE_SERVER,
    DB_SERVER,
    MAX_SERVER_TYPE_COUNT = 3,
}

/// <summary>
/// server ip ,port ,id(在zk产生的唯一id)
/// </summary>
public class ServerInfo
{
    public string Id;     // ZooKeeper 的唯一id
    public string Ip;     // server的ip
    public string Port;   // server的port
}

/// <summary>
/// 登陆状态, 离线，登录中，在线，登出中.写入数据库.
/// </summary>
enum eLoginStatus
{
    offline = 0,
    loging,
    online,
    logout,
}

/// <summary>
/// 登陆流程状态.
/// </summary>
public enum eLogingState : byte
{
    connect = 0,         // 客户端连接
    updateLoging2Db,      // 更新玩家处于登录状态到db
    updateZk2Gate,        // 将登陆信息发给zk
    gateBackSuccess,      // gate做好玩家登陆的准备.
    gateBackFailed,       // gate反馈玩家登陆失败.
    updateOnline2Db,      // 更新玩家已处于在线状态到db.
    backToClient,         // 反馈玩家登陆的信息给客户端.

    loginError,           // 玩家登录失败.
    //disconnected,       // 在玩家登录过程中连接丢失.
}

/// <summary>
/// 登陆错误状态.
/// </summary>
public enum eLoginResult
{
    success = 0,                 // 登录成功
    loginstatus,                 // 登录状态
    accountNotExists,            // 账号不存在.
    wrongPassword,               // 密码错误.
    disconnected,                // 玩家连接丢失.
    unknow,                      // 未知错误.
}

/// <summary>
/// 玩家登录信息.
/// </summary>
public class ClientLoginInfo
{
    public string account;
    public string password;
    public string server;
    public string chanel;
    //public CPhotonServerPeerS peer = null;
    public RpcSession session;
    public string gateId = "";
    public eLogingState state = eLogingState.connect;
    public eLoginResult result = eLoginResult.unknow;
    public string tokenId = "";
    //public bool updateDb = false;  //是否已向db写入登录信息.
    //public bool updateZk = false; // 是否已向zk中写入登录信息,等待gate的反馈.
    //public bool updateDbOnline = false; //是否已向db写入在线信息.
}

/// <summary>
/// Gate Server 状态信息.
/// 
/// </summary>
public class GateInfo
{
    public string id;                  // zk 结点id, 格式是10位的10进制数:(000000005)
    public string ipport;              // ip , port , 格式: 192.168.1.4:4689

    public string loginNode;           // Login --> Gate (玩家登录)
    public string loginLockNode;
    public bool bloginLock = false;

    public string loginComNode;        // Gate --> Login (登录反馈)
    public string loginLockComNode;
    public bool bloginComLock = false;

    public string offlineNode;         // Gate --> Login (玩家下线)
    public string offlineLock;
    public bool bofflineLock = false;
}

/// <summary>
/// 处理一组服务器的用户登录逻辑和
/// </summary>
/// <typeparam name="T"></typeparam>
public class LoginNode<T> : Component<T> where T : ComponentDef, new()
{
    //-------------------------------------------------------------------------
    //private CEtCenterApp<CEntityDef> mpApp = null;
    LoginApp<ComponentDef> mCoApp;
    // path.
    public string LoginQueue;
    public string LoginCompleteQueue;
    public string LoginQueueLock;
    public string LoginCompleteQueueLock;
    public string LoginOfflineQueue;
    public string LoginOfflineQueueLock;
    private string mServerGroupName = "";
    private string DbConnectionStr;
    private MySqlConnection connection = null;
    public string[] ServerPath = new string[(int)eSERVER.MAX_SERVER_TYPE_COUNT];
    public Dictionary<string, ServerInfo>[] ServerInfo = new Dictionary<string, ServerInfo>[(int)eSERVER.MAX_SERVER_TYPE_COUNT];
    /// <summary>
    /// 走登录流程的用户列表.
    /// </summary>
    private ConcurrentDictionary<string, ClientLoginInfo> mLoginPlayerQueue = new ConcurrentDictionary<string, ClientLoginInfo>();
    // Login Server间接与GateServer通讯(玩家登陆),需要额外信息.
    public Dictionary<string, GateInfo> mGateInfo = new Dictionary<string, GateInfo>();
    // 玩家离线.
    public ConcurrentQueue<string> mofflineQueue = new ConcurrentQueue<string>();

    //-------------------------------------------------------------------------
    public override void init()
    {
        mCoApp = (LoginApp<ComponentDef>)Entity.getCacheData("parent");
        for (int index = 0; index < (int)eSERVER.MAX_SERVER_TYPE_COUNT; index++)
        {
            ServerInfo[index] = new Dictionary<string, ServerInfo>();
        }

        LoginQueue = (string)Entity.getCacheData("LoginQueue");
        LoginCompleteQueue = (string)Entity.getCacheData("LoginCompleteQueue");
        LoginQueueLock = (string)Entity.getCacheData("LoginQueueLock");
        LoginCompleteQueueLock = (string)Entity.getCacheData("LoginCompleteQueueLock");
        DbConnectionStr = (string)Entity.getCacheData("ConnectionString");

        LoginOfflineQueue = (string)Entity.getCacheData("PlayerOfflineNode");
        LoginOfflineQueueLock = (string)Entity.getCacheData("PlayerOfflineLock");

        ServerPath[(int)eSERVER.GATE_SERVER] = (string)Entity.getCacheData("GateServer");
        ServerPath[(int)eSERVER.ZONE_SERVER] = (string)Entity.getCacheData("ZoneServer");
        ServerPath[(int)eSERVER.DB_SERVER] = (string)Entity.getCacheData("DBServer");

        mServerGroupName = (string)Entity.getCacheData("ServerGroupName");

        mCoApp.mServerGroup.Add(mServerGroupName, this as LoginNode<ComponentDef>);
        OnInitSingleServer(mServerGroupName);
    }

    //-------------------------------------------------------------------------
    public override void release()
    {
        EbLog.Note("CellApp.release() EntityType=" + Entity.getEntityType() + " EntityRpcId=" + Entity.getEntityRpcId());
    }

    //-------------------------------------------------------------------------
    //  暂时放这里，应该放入独立线程(防止阻塞用户登陆).
    public override void update(float elapsed_tm)
    {
        List<string> del = new List<string>();

        foreach (var player in mLoginPlayerQueue)
        {
            //ClientLoginInfo info = mLoginPlayerQueue.Dequeue();
            ClientLoginInfo info = player.Value;
            if (null == info) continue;

            // 先做断线检测.
            if (info.state != eLogingState.loginError)
            {
                // todo，添加session是否处于连接状态的查询接口
                if (info.session == null)// || !info.session. .Connected)
                {
                    info.state = eLogingState.loginError;
                    info.result = eLoginResult.disconnected;
                }
            }

            if (info.state == eLogingState.connect)
            {
                eLoginResult rtCode = eLoginResult.accountNotExists;
                string sql = string.Format("SELECT AccountName, Password , LoginStatus FROM Account WHERE AccountName='{0}';", info.account);

                EbLog.Note("Login SQL STR :" + sql);

                MySqlCommand cmd = new MySqlCommand(sql, connection);
                MySqlDataReader rdr = null;
                try
                {
                    rdr = cmd.ExecuteReader();
                    if (rdr.Read())
                    {
                        if ((string)rdr["Password"] != info.password)
                        {
                            rtCode = eLoginResult.wrongPassword;
                        }
                        else if ((string)rdr["LoginStatus"] != eLoginStatus.offline.ToString())
                        {
                            rtCode = eLoginResult.loginstatus;
                        }
                        else
                        {
                            // 登录成功.
                            rtCode = eLoginResult.success;
                        }
                        info.result = rtCode;
                    }

                    if (rdr != null)
                    {
                        rdr.Close();
                        rdr.Dispose();
                        rdr = null;
                    }

                    if (rtCode == eLoginResult.success)
                    {
                        // update status
                        sql = string.Format("UPDATE Account SET LoginStatus = '{0}' WHERE AccountName='{1}';", eLoginStatus.loging.ToString(), info.account);

                        EbLog.Note("Login SQL STR :" + sql);

                        MySqlCommand updateCmd = new MySqlCommand(sql, connection);
                        cmd.ExecuteNonQuery();
                    }

                }
                catch (Exception ex)
                {
                    //mLog.ErrorFormat("accoundName:{0}, password:{1} ", info.account, info.password);
                    EbLog.Error(ex.ToString());
                }
                finally
                {
                    cmd.Dispose();
                    cmd = null;

                    if (rdr != null)
                    {
                        if (rdr.IsClosed == false)
                        {
                            rdr.Close();
                        }
                        rdr.Dispose();
                        rdr = null;
                    }
                }

                if (rtCode == ((byte)eLoginResult.success))
                {
                    //info.updateDb = true;
                    info.state = eLogingState.updateLoging2Db;
                }
            }

            if (info.state == eLogingState.updateLoging2Db)
            {
                //mLoginPlayerWaitQueue.Add(info.account, info);

                foreach (var gate in mGateInfo)
                {
                    GateInfo ser = gate.Value;
                    if (!ser.bloginLock)
                    {
                        // 目前只有账号信息和当前longin id放入ZooKeeper.
                        string dt = info.account + "," + mCoApp.NodeId;
                        info.gateId = ser.id;
                        mCoApp.getZk().awriteData(ser.loginNode, dt);
                        EbLog.Note("send to gate node :" + ser.loginNode + ",account:" + dt);
                        mCoApp.getZk().acreate(ser.loginLockNode, "", ZK_CONST.ZOO_EPHEMERAL);
                        EbLog.Note("set remote lock :" + ser.loginLockNode + ",account:" + dt);

                        ser.bloginLock = true;
                        //info.updateZk = true;
                        info.state = eLogingState.updateZk2Gate;
                        break;
                    }
                }
            }

            if (info.state == eLogingState.gateBackSuccess
                || info.state == eLogingState.gateBackFailed
                || info.state == eLogingState.loginError)
            {
                eLoginStatus curState = (info.state == eLogingState.gateBackSuccess) ? eLoginStatus.online : eLoginStatus.offline;
                string sql = string.Format("UPDATE Account SET LoginStatus = '{0}' WHERE AccountName='{1}';", curState, info.account);

                EbLog.Note("Login SQL STR :" + sql);

                MySqlCommand cmd = new MySqlCommand(sql, connection);
                try
                {
                    MySqlCommand updateCmd = new MySqlCommand(sql, connection);
                    cmd.ExecuteNonQuery();
                    info.state = eLogingState.updateOnline2Db;
                }
                catch (Exception ex)
                {
                    //mLog.ErrorFormat("accoundName:{0}, password:{1} ", info.account, info.password);
                    EbLog.Error(ex.ToString());
                }
                finally
                {
                    cmd.Dispose();
                    cmd = null;
                }

                if (info.state == eLogingState.updateOnline2Db)
                {
                    //反馈消息给client.
                    //if (info.peer.Connected)// todo，判定session是否处于连接状态
                    {
                        Dictionary<byte, object> p = new Dictionary<byte, object>();
                        p[0] = curState.ToString();
                        if (curState == eLoginStatus.online)
                        {
                            var list = mGateInfo.Where(gt => gt.Value.id.Equals(info.gateId));
                            p[1] = list.First().Value.ipport;
                            p[2] = info.tokenId;
                        }
                        else
                        {
                            p[1] = info.result.ToString();
                        }

                        // todo，添加session发送任意数据的方法
                        //OperationResponse operation_response = new OperationResponse(1, p);
                        //SendResult r = info.peer.SendOperationResponse(operation_response, new SendParameters { ChannelId = 0 });
                        ////info.session.getRpcPeer().sendEntityRpcData()
                        //if (r != SendResult.Ok)
                        //{
                        //    // Error
                        //}

                        //if (info.peer.Connected)
                        {
                            // 应该断开与客户端的连接.
                            //info.peer.Disconnect();
                        }
                    }

                    info.state = eLogingState.backToClient;
                }

                if (info.state == eLogingState.backToClient)
                {
                    del.Add(info.account);
                }
            }
        }

        foreach (string account in del)
        {
            ClientLoginInfo delPlayer = null;
            mLoginPlayerQueue.TryRemove(account, out delPlayer);
            //delPlayer.peer.Dispose();// todo 连接断开管理
        }

        string offlineaccount = null;
        if (mofflineQueue.TryDequeue(out offlineaccount))
        {
            eLoginStatus curState = eLoginStatus.offline;
            string sql = string.Format("UPDATE Account SET LoginStatus = '{0}' WHERE AccountName='{1}';", curState, offlineaccount);

            EbLog.Error("player offline Login SQL STR :" + sql);
            MySqlCommand cmd = new MySqlCommand(sql, connection);
            try
            {
                MySqlCommand updateCmd = new MySqlCommand(sql, connection);
                cmd.ExecuteNonQuery();
            }
            catch (Exception ex)
            {
                //mLog.ErrorFormat("accoundName:{0} offline failed ", offlineaccount);
                EbLog.Error(ex.ToString());
            }
            finally
            {
                cmd.Dispose();
                cmd = null;
            }
        }
    }

    //-------------------------------------------------------------------------
    public ConcurrentDictionary<string, ClientLoginInfo> LoginPlayerQueue()
    {
        return mLoginPlayerQueue;
    }

    //-------------------------------------------------------------------------
    /// <summary>
    /// 初始化Login Server.
    /// 1. 监听服务器组的其他服务器是否介入.
    /// </summary>
    /// <param name="server"></param>
    private void OnInitSingleServer(string server)
    {
        mCoApp.getZk().subscribeChildChanges(ServerPath[(int)eSERVER.GATE_SERVER]);
        mCoApp.getZk().subscribeChildChanges(ServerPath[(int)eSERVER.ZONE_SERVER]);
        mCoApp.getZk().subscribeChildChanges(ServerPath[(int)eSERVER.DB_SERVER]);
        EbLog.Note("DB connection , server: " + server + "connection string :" + DbConnectionStr);
        connection = new MySqlConnection(DbConnectionStr);

        try
        {
            connection.Open();
        }
        catch (System.Exception ex)
        {
            EbLog.Error("DB connection , server: " + server + " connection failed !");
            EbLog.Error("reason:" + ex);
        }

        if (connection.State == ConnectionState.Open)
        {
            EbLog.Note("DB connection , server: " + server + " connection successed !");
        }
    }

    //-------------------------------------------------------------------------
    /// <summary>
    /// gate server 通过zk反馈的登陆结果.
    /// </summary>
    /// <param name="account">玩家账号</param>
    /// <param name="result">结果</param>
    public void gateBackPlayerLoginResult(string account, string result)
    {
        ClientLoginInfo player = null;
        if (mLoginPlayerQueue.TryGetValue(account, out player))
        {
            if (result == "success")
            {
                player.state = eLogingState.gateBackSuccess;
            }
            else
            {
                player.state = eLogingState.gateBackFailed;
            }
        }
    }

    //-------------------------------------------------------------------------
    /// <summary>
    ///  玩家在gate server下线，需要重新
    /// </summary>
    /// <param name="account">下线玩家账号</param>
    public void onPlayerOffline(string account)
    {
        mofflineQueue.Enqueue(account);
    }

    //-------------------------------------------------------------------------
    /// <summary>
    /// 当前服务器组响应玩家登陆.
    /// </summary>
    /// <param name="account">账号</param>
    /// <param name="password">密码</param>
    /// <param name="chanel">渠道</param>
    /// <returns></returns>
    public bool addLoginPlayer(string account, string password, string chanel, RpcSession s)
    {
        if (mLoginPlayerQueue.ContainsKey(account)) return false;
        ClientLoginInfo player = new ClientLoginInfo();
        player.account = account;
        player.password = password;
        player.server = mServerGroupName;
        player.chanel = chanel;
        player.session = s;
        return mLoginPlayerQueue.TryAdd(account, player);
    }
}
