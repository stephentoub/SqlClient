﻿// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.
// See the LICENSE file in the project root for more information.
using System;
using System.Collections.Generic;
using System.Security.Cryptography;

namespace Microsoft.Data.SqlClient
{
    internal partial class EnclaveDelegate
    {
        private static readonly string GetSerializedAttestationParametersName = "GetSerializedAttestationParameters";

        private static Dictionary<SqlConnectionAttestationProtocol, SqlColumnEncryptionEnclaveProvider> EnclaveProviders = new Dictionary<SqlConnectionAttestationProtocol, SqlColumnEncryptionEnclaveProvider>();

        internal byte[] GetSerializedAttestationParameters(SqlEnclaveAttestationParameters sqlEnclaveAttestationParameters, string enclaveType)
        {
            byte[] attestationProtocolBytes = null;
            byte[] attestationProtocolInputLengthBytes = null;
            byte[] clientDHPublicKeyLengthBytes = null;
            int attestationProtocolInt = sqlEnclaveAttestationParameters.Protocol;

            // attestation protocol
            attestationProtocolBytes = GetUintBytes(enclaveType, attestationProtocolInt, "attestationProtocol");

            if (attestationProtocolBytes == null)
            {
                throw SQL.NullArgumentInternal("attestationProtocolBytes", ClassName, GetSerializedAttestationParametersName);
            }

            // attestationProtocolInput
            byte[] attestationProtocolInputBytes = sqlEnclaveAttestationParameters.GetInput();

            // attestationProtocolInput length
            attestationProtocolInputLengthBytes = GetUintBytes(enclaveType, attestationProtocolInputBytes.Length, "attestationProtocolInputLength");

            if (attestationProtocolInputLengthBytes == null)
            {
                throw SQL.NullArgumentInternal("attestationProtocolInputLengthBytes", ClassName, GetSerializedAttestationParametersName);
            }

            // clientDHPublicKey
            byte[] clientDHPublicKey = sqlEnclaveAttestationParameters.ClientDiffieHellmanKey.Key.Export(CngKeyBlobFormat.EccPublicBlob);

            // clientDHPublicKey length
            clientDHPublicKeyLengthBytes = GetUintBytes(enclaveType, clientDHPublicKey.Length, "clientDHPublicKeyLength");

            if (clientDHPublicKeyLengthBytes == null)
            {
                throw SQL.NullArgumentInternal("clientDHPublicKeyLengthBytes", ClassName, GetSerializedAttestationParametersName);
            }

            return CombineByteArrays(new[] { attestationProtocolBytes, attestationProtocolInputLengthBytes, attestationProtocolInputBytes, clientDHPublicKeyLengthBytes, clientDHPublicKey });
        }

        internal void CreateEnclaveSession(SqlConnectionAttestationProtocol attestationProtocol, string enclaveType, string serverName, string attestationUrl,
            byte[] attestationInfo, SqlEnclaveAttestationParameters attestationParameters)
        {

            lock (_lock)
            {
                SqlColumnEncryptionEnclaveProvider sqlColumnEncryptionEnclaveProvider = GetEnclaveProvider(attestationProtocol, enclaveType);
                long counter;
                SqlEnclaveSession sqlEnclaveSession = null;
                sqlColumnEncryptionEnclaveProvider.GetEnclaveSession(serverName, attestationUrl, out sqlEnclaveSession, out counter);

                if (sqlEnclaveSession != null)
                {
                    return;
                }

                sqlColumnEncryptionEnclaveProvider.CreateEnclaveSession(attestationInfo, attestationParameters.ClientDiffieHellmanKey, attestationUrl, serverName, out sqlEnclaveSession, out counter);

                if (sqlEnclaveSession == null) throw SQL.NullEnclaveSessionReturnedFromProvider(enclaveType, attestationUrl);
            }
        }

        internal EnclavePackage GenerateEnclavePackage(SqlConnectionAttestationProtocol attestationProtocol, Dictionary<int, SqlTceCipherInfoEntry> keysTobeSentToEnclave, string queryText, string enclaveType, string serverName, string enclaveAttestationUrl)
        {

            SqlEnclaveSession sqlEnclaveSession = null;
            long counter;
            try
            {
                GetEnclaveSession(attestationProtocol, enclaveType, serverName, enclaveAttestationUrl, out sqlEnclaveSession, out counter, throwIfNull: true);
            }
            catch (Exception e)
            {
                throw new RetriableEnclaveQueryExecutionException(e.Message, e);
            }

            List<ColumnEncryptionKeyInfo> decryptedKeysToBeSentToEnclave = GetDecryptedKeysToBeSentToEnclave(keysTobeSentToEnclave, serverName);
            byte[] queryStringHashBytes = ComputeQueryStringHash(queryText);
            byte[] keyBytePackage = GenerateBytePackageForKeys(counter, queryStringHashBytes, decryptedKeysToBeSentToEnclave);
            byte[] sessionKey = sqlEnclaveSession.GetSessionKey();
            byte[] encryptedBytePackage = EncryptBytePackage(keyBytePackage, sessionKey, serverName);
            byte[] enclaveSessionHandle = BitConverter.GetBytes(sqlEnclaveSession.SessionId);
            byte[] byteArrayToBeSentToEnclave = CombineByteArrays(new[] { enclaveSessionHandle, encryptedBytePackage });
            return new EnclavePackage(byteArrayToBeSentToEnclave, sqlEnclaveSession);
        }

        internal void InvalidateEnclaveSession(SqlConnectionAttestationProtocol attestationProtocol, string enclaveType, string serverName, string EnclaveAttestationUrl, SqlEnclaveSession enclaveSession)
        {
            SqlColumnEncryptionEnclaveProvider sqlColumnEncryptionEnclaveProvider = GetEnclaveProvider(attestationProtocol, enclaveType);
            sqlColumnEncryptionEnclaveProvider.InvalidateEnclaveSession(serverName, EnclaveAttestationUrl, enclaveSession);
        }

        
        internal SqlEnclaveAttestationParameters GetAttestationParameters(SqlConnectionAttestationProtocol attestationProtocol, string enclaveType, string serverName, string enclaveAttestationUrl)
        {
            SqlColumnEncryptionEnclaveProvider sqlColumnEncryptionEnclaveProvider = GetEnclaveProvider(attestationProtocol, enclaveType);
            return sqlColumnEncryptionEnclaveProvider.GetAttestationParameters();
        }

        private SqlColumnEncryptionEnclaveProvider GetEnclaveProvider(SqlConnectionAttestationProtocol attestationProtocol, string enclaveType)
        {
            SqlColumnEncryptionEnclaveProvider sqlColumnEncryptionEnclaveProvider = null;

            if (!EnclaveProviders.TryGetValue(attestationProtocol, out sqlColumnEncryptionEnclaveProvider))
            {
                switch (attestationProtocol)
                {
                    case SqlConnectionAttestationProtocol.AAS:
                        AzureAttestationEnclaveProvider azureAttestationEnclaveProvider = new AzureAttestationEnclaveProvider();
                        EnclaveProviders[attestationProtocol] = (SqlColumnEncryptionEnclaveProvider)azureAttestationEnclaveProvider;
                        sqlColumnEncryptionEnclaveProvider = EnclaveProviders[attestationProtocol];
                        break;

                    case SqlConnectionAttestationProtocol.HGS:
                        HostGuardianServiceEnclaveProvider hostGuardianServiceEnclaveProvider = new HostGuardianServiceEnclaveProvider();
                        EnclaveProviders[attestationProtocol] = (SqlColumnEncryptionEnclaveProvider)hostGuardianServiceEnclaveProvider;
                        sqlColumnEncryptionEnclaveProvider = EnclaveProviders[attestationProtocol];
                        break;

                    default:
                        break;
                }
            }

            if (sqlColumnEncryptionEnclaveProvider == null)
            {
                throw SQL.EnclaveProviderNotFound(enclaveType, ConvertAttestationProtocolToString(attestationProtocol));
            }

            return sqlColumnEncryptionEnclaveProvider;
        }

        private string ConvertAttestationProtocolToString(SqlConnectionAttestationProtocol attestationProtocol)
        {
            switch (attestationProtocol)
            {
                case SqlConnectionAttestationProtocol.AAS:
                    return "AAS";

                case SqlConnectionAttestationProtocol.HGS:
                    return "HGS";

                default:
                    return "NotSpecified";
            }
        }

        internal void GetEnclaveSession(SqlConnectionAttestationProtocol attestationProtocol, string enclaveType, string serverName, string enclaveAttestationUrl, out SqlEnclaveSession sqlEnclaveSession)
        {
            long counter;
            GetEnclaveSession(attestationProtocol, enclaveType, serverName, enclaveAttestationUrl, out sqlEnclaveSession, out counter, throwIfNull: false);
        }

        // kz to move to netcoreapp
        private void GetEnclaveSession(SqlConnectionAttestationProtocol attestationProtocol, string enclaveType, string serverName, string enclaveAttestationUrl, out SqlEnclaveSession sqlEnclaveSession, out long counter, bool throwIfNull)
        {
            SqlColumnEncryptionEnclaveProvider sqlColumnEncryptionEnclaveProvider = GetEnclaveProvider(attestationProtocol, enclaveType);
            sqlColumnEncryptionEnclaveProvider.GetEnclaveSession(serverName, enclaveAttestationUrl, out sqlEnclaveSession, out counter);

            if (throwIfNull)
            {
                if (sqlEnclaveSession == null)
                    throw SQL.NullEnclaveSessionDuringQueryExecution(enclaveType, enclaveAttestationUrl);
            }
        }
    }
}
