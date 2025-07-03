using System.Collections;
using NUnit.Framework;
using UnityEngine;
using UnityEngine.TestTools;

namespace Yamu.Tests
{
    public class YamuPlayModeTests
    {
        [UnityTest]
        public IEnumerator SimplePlayModeTest()
        {
            // 基本的なテスト
            yield return new WaitForSeconds(0.1f);
            
            // 基本的なアサーション
            Assert.IsTrue(true, "This should always pass");
            
            // Unity コンテキストが利用可能か確認
            Assert.IsNotNull(Application.dataPath, "Application.dataPath should be available");
        }
        
        [UnityTest]
        public IEnumerator GameObjectCreationTest()
        {
            // GameObject の作成とテスト
            var gameObject = new GameObject("TestObject");
            
            yield return new WaitForSeconds(0.1f);
            
            Assert.IsNotNull(gameObject, "GameObject should be created");
            Assert.AreEqual("TestObject", gameObject.name, "GameObject name should match");
            
            // クリーンアップ
            Object.DestroyImmediate(gameObject);
        }
        
        [UnityTest]
        public IEnumerator ComponentTest()
        {
            // コンポーネントのテスト
            var gameObject = new GameObject("TestObject");
            var rigidbody = gameObject.AddComponent<Rigidbody>();
            
            yield return new WaitForSeconds(0.1f);
            
            Assert.IsNotNull(rigidbody, "Rigidbody component should be added");
            Assert.AreEqual(1.0f, rigidbody.mass, "Default mass should be 1.0");
            
            // クリーンアップ
            Object.DestroyImmediate(gameObject);
        }
    }
}