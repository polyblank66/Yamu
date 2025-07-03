using NUnit.Framework;
using UnityEngine;

[TestFixture]
public class YamuTests
{
    [Test]
    public void PassingTest1()
    {
        Assert.AreEqual(4, 2 + 2);
    }

    [Test]
    public void PassingTest2()
    {
        Assert.IsTrue(true);
    }

    [Test]
    public void FailingTest1()
    {
        Assert.AreEqual(5, 2 + 2, "This should fail: 2 + 2 = 4, not 5");
    }

    [Test]
    public void FailingTest2()
    {
        Assert.IsFalse(true, "This should fail: true is not false");
    }

    [Test]
    public void PassingTest3()
    {
        var vector = new Vector3(1, 2, 3);
        Assert.AreEqual(1, vector.x);
    }
}