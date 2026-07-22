using RestoreGuard.Core;
using RestoreGuard.Core.Model;
using System;
using System.Collections.Generic;
using System.Linq;
using Xunit;

namespace RestoreGuard.Checks.Tests;

public class DashboardRegistrationDriftCheckTests
{
    private static LabInventory CreateInventory(
        bool dashboardReachable = true,
        List<DashboardService>? services = null,
        List<DockerHost>? hosts = null)
    {
        return new LabInventory(DateTimeOffset.MinValue, [], [], [])
        {
            Dashboard = new DashboardRegistry
            {
                IsReachable = dashboardReachable,
                Services = services ?? new List<DashboardService>()
            },
            Hosts = hosts ?? new List<DockerHost>(),
            ClusterHost = "k3s-master"
        };
    }

    private static DockerHost CreateHost(string ip, params RunningContainer[] containers)
    {
        return new DockerHost
        {
            IpAddress = ip,
            Containers = containers.ToList()
        };
    }

    private static RunningContainer CreateContainer(string name, params PublishedPort[] ports)
    {
        return new RunningContainer
        {
            Name = name,
            Ports = ports.ToList()
        };
    }

    private static PublishedPort CreatePort(int hostPort, int containerPort, string protocol = "tcp")
    {
        return new PublishedPort
        {
            HostPort = hostPort,
            ContainerPort = containerPort,
            Protocol = protocol
        };
    }

    [Fact]
    public void UnregisteredContainer_ProducesFinding()
    {
        // Arrange
        var inventory = CreateInventory(
            services: new List<DashboardService>
            {
                new DashboardService { Name = "webapp", Url = "http://192.168.1.99:8080" },
                new DashboardService { Name = "api", Url = "http://192.168.1.142" }
            },
            hosts: new List<DockerHost>
            {
                CreateHost("192.168.1.99", 
                    CreateContainer("webapp", CreatePort(8080, 80)),
                    CreateContainer("api", CreatePort(80, 80)),
                    CreateContainer("unregistered", CreatePort(3000, 3000))
                ),
                CreateHost("192.168.1.142")
            });

        var check = new DashboardRegistrationDriftCheck();

        // Act
        var findings = check.Evaluate(inventory).ToList();

        // Assert
        Assert.Single(findings);
        Assert.Equal("dashboard-registration-drift/unregistered", findings[0].RuleId);
        Assert.Equal("192.168.1.99/unregistered", findings[0].Service);
    }

    [Fact]
    public void DashboardUnreachable_RedFinding()
    {
        // Arrange
        var inventory = CreateInventory(
            dashboardReachable: false,
            hosts: new List<DockerHost>
            {
                CreateHost("192.168.1.99", CreateContainer("webapp", CreatePort(8080, 80)))
            });

        var check = new DashboardRegistrationDriftCheck();

        // Act
        var findings = check.Evaluate(inventory).ToList();

        // Assert
        Assert.Single(findings);
        Assert.Equal("dashboard-registration-drift/unreachable", findings[0].RuleId);
        Assert.Equal(Severity.Red, findings[0].Severity);
    }

    [Fact]
    public void MultipleHostsWithSameContainerName_BothMatchedByName()
    {
        // Arrange
        var inventory = CreateInventory(
            services: new List<DashboardService>
            {
                new DashboardService { Name = "dashboard", Url = "http://192.168.1.99:80" }
            },
            hosts: new List<DockerHost>
            {
                CreateHost("192.168.1.99", CreateContainer("dashboard", CreatePort(80, 80))),
                CreateHost("192.168.1.142", CreateContainer("dashboard", CreatePort(80, 80)))
            });

        var check = new DashboardRegistrationDriftCheck();

        // Act
        var findings = check.Evaluate(inventory).ToList();

        // Assert - name match is host-agnostic: both "dashboard" containers match the "dashboard" service
        Assert.Empty(findings);
    }

    [Fact]
    public void ContainerWithNoPorts_NoFinding()
    {
        // Arrange
        var inventory = CreateInventory(
            hosts: new List<DockerHost>
            {
                CreateHost("192.168.1.99", CreateContainer("internal-worker")) // No ports
            });

        var check = new DashboardRegistrationDriftCheck();

        // Act
        var findings = check.Evaluate(inventory).ToList();

        // Assert
        Assert.Empty(findings);
    }

    [Fact]
    public void DefaultHttpPortMatched()
    {
        // Arrange
        var inventory = CreateInventory(
            services: new List<DashboardService>
            {
                new DashboardService { Name = "site", Url = "http://192.168.1.99" } // No explicit port
            },
            hosts: new List<DockerHost>
            {
                CreateHost("192.168.1.99", CreateContainer("site", CreatePort(80, 80)))
            });

        var check = new DashboardRegistrationDriftCheck();

        // Act
        var findings = check.Evaluate(inventory).ToList();

        // Assert
        Assert.Empty(findings);
    }

    [Fact]
    public void DefaultHttpsPortMatched()
    {
        // Arrange
        var inventory = CreateInventory(
            services: new List<DashboardService>
            {
                new DashboardService { Name = "secure-site", Url = "https://192.168.1.99" } // No explicit port
            },
            hosts: new List<DockerHost>
            {
                CreateHost("192.168.1.99", CreateContainer("secure-site", CreatePort(443, 443)))
            });

        var check = new DashboardRegistrationDriftCheck();

        // Act
        var findings = check.Evaluate(inventory).ToList();

        // Assert
        Assert.Empty(findings);
    }

    [Fact]
    public void CaseInsensitiveNameMatch()
    {
        // Arrange
        var inventory = CreateInventory(
            services: new List<DashboardService>
            {
                new DashboardService { Name = "WebApp", Url = "" } // Different case
            },
            hosts: new List<DockerHost>
            {
                CreateHost("192.168.1.99", CreateContainer("webapp", CreatePort(8080, 80)))
            });

        var check = new DashboardRegistrationDriftCheck();

        // Act
        var findings = check.Evaluate(inventory).ToList();

        // Assert
        Assert.Empty(findings);
    }

    [Fact]
    public void ExactNameMatchRequired_NoPartialMatches()
    {
        // Arrange
        var inventory = CreateInventory(
            services: new List<DashboardService>
            {
                new DashboardService { Name = "dashboard-main", Url = "" } // Full name
            },
            hosts: new List<DockerHost>
            {
                CreateHost("192.168.1.99", CreateContainer("dashboard", CreatePort(80, 80))) // Partial
            });

        var check = new DashboardRegistrationDriftCheck();

        // Act
        var findings = check.Evaluate(inventory).ToList();

        // Assert
        Assert.Single(findings);
        Assert.Equal("dashboard-registration-drift/unregistered", findings[0].RuleId);
    }

    [Fact]
    public void IpSubstringDoesNotMatch()
    {
        // Arrange - URL with 192.168.1.9 should NOT match container on 192.168.1.99
        var inventory = CreateInventory(
            services: new List<DashboardService>
            {
                new DashboardService { Name = "svc", Url = "http://192.168.1.9:8080" }
            },
            hosts: new List<DockerHost>
            {
                CreateHost("192.168.1.99", CreateContainer("webapp", CreatePort(8080, 80)))
            });

        var check = new DashboardRegistrationDriftCheck();

        // Act
        var findings = check.Evaluate(inventory).ToList();

        // Assert
        Assert.Single(findings);
        Assert.Equal("192.168.1.99/webapp", findings[0].Service);
    }

    [Fact]
    public void PortMismatchDoesNotRegister()
    {
        // Arrange - service on different port should NOT match container
        var inventory = CreateInventory(
            services: new List<DashboardService>
            {
                new DashboardService { Name = "svc", Url = "http://192.168.1.99:8080" } // Different port
            },
            hosts: new List<DockerHost>
            {
                CreateHost("192.168.1.99", CreateContainer("webapp", CreatePort(80, 80))) // Port 80
            });

        var check = new DashboardRegistrationDriftCheck();

        // Act
        var findings = check.Evaluate(inventory).ToList();

        // Assert
        Assert.Single(findings);
        Assert.Equal("192.168.1.99/webapp", findings[0].Service);
    }

    [Fact]
    public void HostScopedSuppressionKey()
    {
        // Arrange - same container name on different hosts should have different suppression keys
        var inventory = CreateInventory(
            services: new List<DashboardService>(),
            hosts: new List<DockerHost>
            {
                CreateHost("192.168.1.99", CreateContainer("webapp", CreatePort(8080, 80))),
                CreateHost("192.168.1.100", CreateContainer("webapp", CreatePort(8080, 80)))
            });

        var check = new DashboardRegistrationDriftCheck();

        // Act
        var findings = check.Evaluate(inventory).ToList();

        // Assert
        Assert.Equal(2, findings.Count);
        Assert.Equal("192.168.1.99/webapp", findings[0].Service);
        Assert.Equal("192.168.1.100/webapp", findings[1].Service);
    }

    [Fact]
    public void NullDashboardServiceUrl_NoCrash()
    {
        // Arrange - null URL should be handled gracefully
        var inventory = CreateInventory(
            services: new List<DashboardService>
            {
                new DashboardService { Name = "null-url-service", Url = null }
            },
            hosts: new List<DockerHost>
            {
                CreateHost("192.168.1.99", CreateContainer("webapp", CreatePort(8080, 80)))
            });

        var check = new DashboardRegistrationDriftCheck();

        // Act & Assert - should not throw
        var findings = check.Evaluate(inventory).ToList();
        // Just verifying it didn't throw is sufficient
    }

    [Fact]
    public void MultiPortContainer_AnyPortMatchRegisters()
    {
        // Arrange - container with multiple ports, one matches
        var inventory = CreateInventory(
            services: new List<DashboardService>
            {
                new DashboardService { Name = "partial-match", Url = "http://192.168.1.99:8080" }
            },
            hosts: new List<DockerHost>
            {
                CreateHost("192.168.1.99", 
                    CreateContainer("multi-port", 
                        CreatePort(8080, 80), // This port matches
                        CreatePort(9000, 9000)  // This port doesn't)
                    )
                )
            });

        var check = new DashboardRegistrationDriftCheck();

        // Act
        var findings = check.Evaluate(inventory).ToList();

        // Assert
        Assert.Empty(findings); // At least one port matches
    }
}
