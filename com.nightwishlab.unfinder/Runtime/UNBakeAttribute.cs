using System;

/// <summary>
/// UNFinder 베이크 패스에 포함할 MonoBehaviour 클래스에 지정합니다.
/// 이 특성이 붙은 컴포넌트를 하나 이상 가진 GameObject만
/// 빌드 시점에 UNTracker가 부착됩니다.
///
/// 사용 예:
///   [UNBake]
///   public class Enemy : MonoBehaviour { ... }
/// </summary>
[AttributeUsage(AttributeTargets.Class, Inherited = true, AllowMultiple = false)]
public sealed class UNBakeAttribute : Attribute { }
